using LibVLCSharp.Shared;

namespace SongServer.Services;

/// <summary>
/// LibVLCSharp-based implementation of AudioPlayer for audio playback.
/// </summary>
internal class VlcAudioPlayer : AudioPlayer, IDisposable
{
	private readonly LibVLC _vlc;
	private readonly MediaPlayer _player;
	private readonly ILogger _logger;
	private bool _disposed = false;

	static VlcAudioPlayer()
	{
		Core.Initialize();
	}

	public VlcAudioPlayer(ILogger<VlcAudioPlayer> logger) : base(logger)
	{
		_logger = logger;
		_vlc = new();
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

		//_player.SetAudioOutput("alsa");
		//_player.SetOutputDevice("hw:CARD=Headphones,DEV=0", null);

		_player.EndReached += HandleSongEnd;
		_player.Playing += HandlePlaying;
		_player.Paused += HandlePaused;
		_player.Stopped += HandleStopped;
		_player.MediaChanged += HandleMediaChanged;
		_player.PositionChanged += HandlePositionChanged;
	}

	~VlcAudioPlayer() => Dispose(false);

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (!_disposed) {
			if (disposing) {
				_player.Dispose();
				_vlc.Dispose();
			}
			_disposed = true;
		}
	}

	public override void Play()
	{
		_logger.LogInformation("Play() called. State: {state}", _currentState);

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

	public override void Pause() => _player.Pause();

	public override void Stop() => _player.Stop();

	protected override void Play(Uri uri)
	{
		_logger.LogInformation("Play requested for {Uri}", uri);
		_player.Play(new Media(_vlc, uri));
	}

	private void HandleSongEnd(object? sender, EventArgs eventArgs) => Task.Run(Advance);

	private void HandlePlaying(object? sender, EventArgs eventArgs) => OnPlayerStateChanged(State.Playing);

	private void HandlePaused(object? sender, EventArgs eventArgs) => OnPlayerStateChanged(State.Paused);

	private void HandleStopped(object? sender, EventArgs eventArgs) => OnPlayerStateChanged(State.Stopped);

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

		OnPositionChanged(position);
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

		if (media == null) {
			return;
		}

		var song = new SongMetadata(
			[media.Meta(MetadataType.Artist) ?? string.Empty],
			media.Meta(MetadataType.Album),
			media.Meta(MetadataType.Title),
			int.TryParse(media.Meta(MetadataType.TrackNumber), out var tn) ? tn : -1,
			int.TryParse(media.Meta(MetadataType.TrackTotal), out var tt) ? tt : -1,
			media.Meta(MetadataType.ArtworkURL),
			TimeSpan.FromMilliseconds(media.Duration)
		);

		OnMediaMetadataAvailable(song);
	}

	/// <summary>
	/// Displays available audio output modules and devices.
	/// </summary>
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
}
