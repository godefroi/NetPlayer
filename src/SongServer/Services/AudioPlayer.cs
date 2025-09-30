using LibVLCSharp.Shared;

namespace SongServer.Services;

internal class AudioPlayer : IDisposable
{
	private readonly LibVLC _vlc;
	private readonly MediaPlayer _player;
	private readonly Lock _stateLock = new();
	private readonly Lock _queueLock = new();
	private readonly ILogger _logger;
	private readonly Queue<Uri> _queue = [];
	private Media? _currentMedia;
	private bool _disposed = false;
	private Playlist _list = Playlist.Empty;
	private PlaylistState _state = PlaylistState.Empty;
	private PlayerState _playerState = new(State.Stopped, null, 0, 0);

	static AudioPlayer()
	{
		Core.Initialize();
	}

	public AudioPlayer(ILogger<AudioPlayer> logger)
	{
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
	}

	~AudioPlayer()
	{
		Dispose(false);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposed) {
			if (disposing) {
				lock (_stateLock) {
					_currentMedia?.Dispose();
					_currentMedia = null;
				}

				_player.EndReached -= HandleSongEnd;
				_player.Playing -= HandlePlaying;
				_player.Paused -= HandlePaused;
				_player.Stopped -= HandleStopped;
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

	public Task Play()
	{
		lock (_stateLock) {
			// if we're already playing, then we don't have to do anything
			if (_playerState.State == State.Playing) {
				return Task.CompletedTask;
			}

			// if we're paused (i.e. we have media), just unpause it
			if (_playerState.State == State.Paused) {
				_player.SetPause(false);
				return Task.CompletedTask;
			}

			// if we've set our current media, play that, otherwise
			// advance to the next song (in whatever way)
			if (_currentMedia != null) {
				_player.Play(_currentMedia);
			} else if (_list != null) {
				return Advance();
			}

			return Task.CompletedTask;
		}
	}

	public void Pause() 
	{
		lock (_stateLock) {
			_player.Pause();
			UpdatePlayerState(State.Paused);
		}
	}

	public async Task Stop()
	{
		if (_playerState.State != State.Stopped) {
			await Task.Run(() => {
				lock (_stateLock) {
					if (_playerState.State != State.Stopped) {
						_player.Stop();
						_player.Media = null;
						_currentMedia?.Dispose();
						_currentMedia = null;
						_playerState = _playerState with {
							Song = null,
						};
						UpdatePlayerState(State.Stopped);
					}
				}
			});
		}
	}

	public Task Advance()
	{
		// NOTE NOTE NOTE
		// we might already be locking on the state lock; don't deadlock!
		lock (_queueLock) {
			_logger.LogDebug("Advancing to the next song. State: {State}", _playerState.State);

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
				return Play(_queue.Dequeue());
			} else {
				_logger.LogWarning("Cannot advance, nothing to play!");
				return Task.CompletedTask;
			}
		}
	}

	public void SetPlaylist(Playlist list, PlaylistState state)
	{
		// we lock the state outside, because that's the order that we lock
		// when we're calling Advance() and Play() and whatever...
		lock (_stateLock) {
			lock (_queueLock) {
				_list  = list;
				_state = state;

				_playerState = _playerState with { ListLength = _list.Entries.Count };
			}
		}
	}

	public PlayerState GetState()
	{
		lock (_stateLock) {
			if (_playerState.Song != null && _currentMedia != null) {
				
			}
			// Update the current position if we're playing/paused and have media
			if (_currentMedia != null && _playerState.Song != null && (_playerState.State == State.Playing || _playerState.State == State.Paused)) {
				var len = TimeSpan.FromMilliseconds(_currentMedia.Duration);
				var currentPosition = len * _player.Position;
				
				var updatedSong = _playerState.Song with { Position = currentPosition };
				return _playerState with { Song = updatedSong };
			}
			
			return _playerState;
		}
	}

	private async Task Play(Uri uri, CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Play requested for {Uri}", uri);

		// stop, in case we're already playing
		await Stop();

		Task parseTask;

		lock (_stateLock) {
			_currentMedia = new Media(_vlc, uri);
			_currentMedia.ParsedChanged += HandleMediaParse;
			parseTask = _currentMedia.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.ParseNetwork | MediaParseOptions.FetchLocal | MediaParseOptions.FetchNetwork, 1000, cancellationToken);
			_player.Play(_currentMedia);
		}

		// might as well wait for the parse to finish once...
		await parseTask;

		using var tcs = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		// wait up to 1000ms for the player to start playing
		tcs.CancelAfter(1000);

		// here we need to wait until the player thinks it's playing, plus
		// however much metadata we want to have been parsed is parsed
		while (!tcs.IsCancellationRequested) {
			lock (_stateLock) {
				if (_playerState.State == State.Playing) {
					break;
				}
			}

			await Task.Delay(10, tcs.Token);
		}
	}

	private void HandleSongEnd(object? sender, EventArgs eventArgs) => _ = Task.Run(Advance);

	private void HandlePlaying(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Playing);

	private void HandlePaused(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Paused);

	private void HandleStopped(object? sender, EventArgs eventArgs) => UpdatePlayerState(State.Stopped);

	private void HandleMediaParse(object? sender, MediaParsedChangedEventArgs args)
	{
		lock (_stateLock) {
			if (_currentMedia == null) {
				_playerState = _playerState with {
					Song = null,
				};

				return;
			}

			_playerState = _playerState with {
				Song = new(
					_currentMedia.Meta(MetadataType.Artist),
					_currentMedia.Meta(MetadataType.Album),
					_currentMedia.Meta(MetadataType.Title),
					int.TryParse(_currentMedia.Meta(MetadataType.TrackNumber), out var tn) ? tn : -1,
					int.TryParse(_currentMedia.Meta(MetadataType.TrackTotal), out var tt) ? tt : -1,
					_currentMedia.Meta(MetadataType.ArtworkURL),
					TimeSpan.FromMilliseconds(_currentMedia.Duration),
					TimeSpan.Zero)
			};

			_logger.LogInformation("Current Song: {Artist} - {Title} from {Album} (Track {TrackNumber}/{TrackTotal}) - Duration: {Duration}",
				_playerState.Song?.Artist,
				_playerState.Song?.Title,
				_playerState.Song?.Album,
				_playerState.Song?.TrackNumber,
				_playerState.Song?.TrackTotal,
				_playerState.Song?.Duration);
		}
	}

	private void UpdatePlayerState(State state)
	{
		lock (_stateLock) {
			_playerState = _playerState with {
				State = state,
			};
		}
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

	public enum State
	{
		Stopped,
		Paused,
		Playing,
	}
}
