using LibVLCSharp.Shared;
using Microsoft.AspNetCore.SignalR;

namespace SongServer.Services;

internal class AudioPlayer : IDisposable
{
	private readonly LibVLC _vlc;
	private readonly MediaPlayer _player;
	private readonly Lock _stateLock = new();
	private readonly IHubContext<PlayerHub, IPlayerHub> _hub;
	private readonly ILogger _logger;
	private readonly Queue<Uri> _queue = [];
	private bool _disposed = false;
	private Playlist _list = Playlist.Empty;
	private PlaylistState _state = PlaylistState.Empty;
	private SongMetadata? _currentSong = null;
	private TimeSpan _currentPosition = TimeSpan.FromSeconds(-1);
	private State _currentState = State.Stopped;

	static AudioPlayer()
	{
		Core.Initialize();
	}

	public AudioPlayer(IHubContext<PlayerHub, IPlayerHub> hub, ILogger<AudioPlayer> logger)
	{
		_hub    = hub;
		_logger = logger;
		_vlc    = new();
		_player = new(_vlc) {
			Volume = 45,
		};

		_vlc.Log += (_, e) => {
			_logger.Log(e.Level switch {
				LibVLCSharp.Shared.LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
				LibVLCSharp.Shared.LogLevel.Notice => Microsoft.Extensions.Logging.LogLevel.Information,
				LibVLCSharp.Shared.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
				LibVLCSharp.Shared.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
				_ => Microsoft.Extensions.Logging.LogLevel.None
			}, "VLC: {message}", e.Message);
		};

		_player.EndReached += HandleSongEnd;
		_player.Playing += HandlePlaying;
		_player.Paused += HandlePaused;
		_player.Stopped += HandleStopped;
		_player.MediaChanged += HandleMediaChanged;
		_player.PositionChanged += HandlePositionChanged;
	}

	~AudioPlayer() => Dispose(false);

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposed) {
			if (disposing) {
				_player.Dispose();
				_vlc.Dispose();
			}

			// unmanaged resources if any
			_disposed = true;
		}
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	public void Play()
	{
		_logger.LogInformation("Play() called. IsPlaying: {playing} WillPlay: {willPlay} State: {state}", _player.IsPlaying, _player.WillPlay, _currentState);

		// if we will play (i.e. we have media), just do that
		// if we're paused, this will resume
		// if we stopped (we're not paused), WillPlay is false
		if (_player.WillPlay) {
			_player.Play();
			return;
		}

		// otherwise, we need to get new media and play that
		Advance();
	}

	public void Pause() => _player.Pause();

	public void Stop() => _player.Stop();

	public void Advance()
	{
		lock (_stateLock) {
			// if we have no songs in the queue, then put the next song from
			// the playlist into the queue, if there are any songs in it
			if (_queue.Count == 0 && _list.Entries.Count > 0) {
				// check if the index is now out of bounds
				if (++_state.CurrentIndex >= _list.Entries.Count) {
					_state.CurrentIndex = 0;
				}

				// add the next song to the queue
				_queue.Enqueue(_list.Entries[_state.CurrentIndex]);
			}

			// now, play the next song in the queue
			if (_queue.Count > 0) {
				_logger.LogDebug("Advancing to the next song in the queue");
				Play(_queue.Dequeue());
			} else {
				_logger.LogWarning("Cannot advance, nothing to play!");
			}
		}
	}

	public void SetPlaylist(Playlist list, PlaylistState state)
	{
		lock (_stateLock) {
			_list  = list;
			_state = state;
		}
	}

	public (State State, SongMetadata? Song, TimeSpan? Position) GetState()
	{
		lock (_stateLock) {
			return (_currentState, _currentSong, _currentPosition);
		}
	}

	public void Enqueue(Uri uri)
	{
		lock (_stateLock) {
			_queue.Enqueue(uri);
		}
	}

	private void Play(Uri uri)
	{
		_logger.LogInformation("Play requested for {Uri}", uri);

		_player.Play(new Media(_vlc, uri));
	}

	private void HandleSongEnd(object? sender, EventArgs eventArgs) => Task.Run(Advance);

	private void HandlePlaying(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Playing);

	private void HandlePaused(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Paused);

	private void HandleStopped(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Stopped);

	private void HandleMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs eventArgs)
	{
		var media = eventArgs.Media;

		// LibVLCSharp events are exceedingly weird; we hook the event, lose the
		// instance, but the event still happens; the instance isn't the sender,
		// there's another class (EventManager) that ends up sending the event
		media.ParsedChanged += HandleMediaParse;

		_ = media.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.ParseNetwork | MediaParseOptions.FetchLocal | MediaParseOptions.FetchNetwork, 1000);
	}

	private void HandlePositionChanged(object? sender, MediaPlayerPositionChangedEventArgs eventArgs)
	{
		using var media = _player.Media;

		if (media == null) {
			// dunno what to do here... presumably things have gone
			// quite wrong.
			return;
		}

		var duration = media.Duration;
		var position = TimeSpan.FromMilliseconds(duration) * eventArgs.Position;
		var curState = State.Stopped;
		var sendPos  = false;

		lock (_stateLock) {
			// only send(/update) the event if position changed by enough
			if (Math.Abs((position - _currentPosition).TotalSeconds) > 1.0) {
				_currentPosition = position;
				curState = _currentState;
				sendPos = true;
			}
		}

		if (sendPos) {
			_hub.Clients.All.UpdatePosition(curState, position);
		}
	}

	private void HandleMediaParse(object? sender, MediaParsedChangedEventArgs args)
	{
		if (args.ParsedStatus != MediaParsedStatus.Done) {
			return;
		}

		// we're just going to assume the player's current media is the one that
		// was parsed... if that's incorrect, we'll get called again Real Soon Now
		// for the new media and fix our mistakes
		using var media = _player.Media;

		var song = media == null ? null : new SongMetadata(
			media.Meta(MetadataType.Artist),
			media.Meta(MetadataType.Album),
			media.Meta(MetadataType.Title),
			int.TryParse(media.Meta(MetadataType.TrackNumber), out var tn) ? tn : -1,
			int.TryParse(media.Meta(MetadataType.TrackTotal), out var tt) ? tt : -1,
			media.Meta(MetadataType.ArtworkURL),
			TimeSpan.FromMilliseconds(media.Duration)
		);

		lock (_stateLock) {
			_currentSong = song;
		}

		_hub.Clients.All.UpdateMedia(song);

		if (song != null) {
			_logger.LogInformation("Current Song: {Artist} - {Title} from {Album} (Track {TrackNumber}/{TrackTotal}) - Duration: {Duration}",
				song.Artist,
				song.Title,
				song.Album,
				song.TrackNumber,
				song.TrackTotal,
				song.Duration);
		}
	}

	private void UpdatePlayerState(State state)
	{
		var curPos = default(TimeSpan?);

		lock (_stateLock) {
			_currentState = state;
			curPos = _currentPosition;
		}

		_hub.Clients.All.UpdatePosition(state, curPos);
	}

	public static void DisplayOutputs()
	{
		using var vlc = new LibVLC();

		Console.WriteLine("Audio Output Modules:");
		foreach (var ao in vlc.AudioOutputs) {
			Console.WriteLine($"  {ao.Name}: {ao.Description}");

			foreach (var od in vlc.AudioOutputDevices(ao.Name)) {
				Console.WriteLine($"    {od.DeviceIdentifier}: {od.Description}");
			}
		}
		Console.WriteLine();
	}

	internal class PlaylistState
	{
		public static PlaylistState Empty { get; } = new()
		{
			Path = string.Empty,
			Hash = string.Empty
		};

		public required string Path { get; init; }
		public required string Hash { get; init; }
		public int? ShuffleSeed { get; init; }
		public int CurrentIndex { get; set; } = -1;
	}
}
