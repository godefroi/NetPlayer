using System.Text;
using System.Text.Json;
using ManagedBass;
using ManagedBass.Opus;
using Microsoft.Extensions.Options;
using SongServer.Configuration;

namespace SongServer.Services;

internal class BassAudioPlayer : AudioPlayer, IDisposable
{
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _semaphore = new(1, 1);
	private readonly LibraryService _library;
	private readonly CancellationTokenSource _statusToken = new();
	private readonly Task _statusTask;
	private readonly PlayerOptions _options;
	private int _stream = 0;
	private TimeSpan _lastPos = TimeSpan.Zero;
	private bool _disposed = false;

	public BassAudioPlayer(ILogger<BassAudioPlayer> logger, IOptions<PlayerOptions> options, LibraryService library) : base(logger)
	{
		_logger  = logger;
		_library = library;
		_options = options.Value;

		try {
			var deviceIdx = Bass.DefaultDevice;

			if (!string.IsNullOrWhiteSpace(_options.Device)) {
				var devices = Enumerable.Range(0, Bass.DeviceCount).Select(Bass.GetDeviceInfo).ToList();

				deviceIdx = devices.FindIndex(di => di.Name == _options.Device);

				if (deviceIdx == -1) {
					_logger.LogWarning("Device [{device}] was not found. Using default audio device.", _options.Device);
				} else {
					_logger.LogInformation("Using audio device [{device}].", devices[deviceIdx].Name);
				}
			} else {
				_logger.LogInformation("Using default audio device.");
			}

			// Initialize BASS with the specified device (or default)
			if (!Bass.Init(deviceIdx)) {
				throw new InvalidOperationException($"Failed to initialize BASS: {Bass.LastError}");
			}

			//Bass.Configure(ManagedBass.Configuration.GlobalStreamVolume, 1500);

			_statusTask = MonitorStatus();

			_logger.LogInformation("BASS initialized successfully");
		} catch (Exception ex) {
			_logger.LogError(ex, "Failed to initialize BASS audio player");
			throw;
		}
	}

	public override async Task<bool> Pause()
	{
		await _semaphore.WaitAsync();

		try {
			if (_stream == 0) {
				_logger.LogWarning("No stream to pause");
				return false;
			}

			if (!Bass.ChannelPause(_stream)) {
				_logger.LogError("Failed to pause: {error}", Bass.LastError);
				return false;
			}

			OnPlayerStateChanged(State.Paused);
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error pausing playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	protected override async Task<bool> Resume()
	{
		await _semaphore.WaitAsync();

		try {
			if (_stream == 0) {
				_logger.LogWarning("No stream to resume");
				return false;
			}

			if (!Bass.ChannelPlay(_stream)) {
				_logger.LogError("Failed to resume: {error}", Bass.LastError);
				return false;
			}

			OnPlayerStateChanged(State.Playing);
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error resuming playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	public override async Task<bool> Stop()
	{
		await _semaphore.WaitAsync();

		try {
			if (_stream != 0) {
				Bass.ChannelStop(_stream);
				Bass.StreamFree(_stream);
				_stream = 0;
			}

			OnPlayerStateChanged(State.Stopped);
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error stopping playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	protected override async Task<bool> Play(SongReference song)
	{
		await _semaphore.WaitAsync();

		try {
			// Stop any currently playing stream
			if (_stream != 0) {
				Bass.ChannelStop(_stream);
				Bass.StreamFree(_stream);
				_stream = 0;
			}

			_logger.LogInformation("Loading file: {reference}", song);

			var dbSong    = await _library.GetSong(song) ?? throw new InvalidOperationException("The specified song does not exist.");
			var songPath  = Path.Combine(dbSong.SourcePath, dbSong.RelativePath);
			var streamUri = new UriBuilder(Uri.UriSchemeHttp, "localhost", 5000, $"/library/stream/{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(song)))}").Uri; // TODO: don't hard-code this

			// Create a BASS stream from the file path
			_stream = Path.GetExtension(songPath) switch {
				".opus" => BassOpus.CreateStream(streamUri.ToString(), 0, BassFlags.Default /*| BassFlags.BassLoudnessCurrent*/, (_, _, _) => { }),
				_ => Bass.CreateStream(streamUri.ToString(), 0, BassFlags.Default /*| BassFlags.BassLoudnessCurrent*/, (_, _, _) => { }),
			};

			if (_stream == 0) {
				_logger.LogError("Failed to create stream: {error}", Bass.LastError);
				return false;
			}

			// Start playback
			if (!Bass.ChannelPlay(_stream)) {
				_logger.LogError("Failed to play stream: {error}", Bass.LastError);
				Bass.StreamFree(_stream);
				_stream = 0;
				return false;
			}

			OnPlayerStateChanged(State.Playing, new SongMetadata(dbSong.Performers, dbSong.Album, dbSong.Title, dbSong.TrackNumber, dbSong.TrackCount, "about:empty", dbSong.Duration));

			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error playing file");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	private async Task MonitorStatus()
	{
		try {
			while (!_statusToken.IsCancellationRequested) {
				_semaphore.Wait();

				try {
					if (_stream != 0) {
						var position = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);

						if (position >= 0) {
							var newPos = TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_stream, position));

							if (newPos != _lastPos) {
								OnPositionChanged(newPos);
								_lastPos = newPos;
							}
						}

						if (Bass.ChannelIsActive(_stream) == PlaybackState.Stopped) {
							// if we got here, then the song finished (if Stop() had been called,
							// we wouldn't have a stream anymore)

							// tear down any BASS resources we've got tied up
							Bass.StreamFree(_stream);
							_stream = 0;

							// notify the base class we're ready to advance (on a new
							// thread, because we're holding the semaphore, and the
							// base class is surely going to call Play() on us)
							_ = OnPlaybackFinished();
						}
					} else {
						// ensure we zero out position and state if _stream is recently zeroed out
					}
				} finally {
					_semaphore.Release();
				}

				await Task.Delay(250, _statusToken.Token);
			}
		} catch (OperationCanceledException) {
			// Expected when stopping
		} catch (Exception ex) {
			_logger.LogCritical(ex, "Error monitoring playback state");
		}
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		_statusToken?.Cancel();
		_statusTask.Wait();
		_statusToken?.Dispose();

		if (_stream != 0) {
			Bass.ChannelStop(_stream);
			Bass.StreamFree(_stream);
			_stream = 0;
		}

		if (!Bass.Free()) {
			_logger.LogError("Error freeing BASS library instance: {err}", Bass.LastError);
		}

		_semaphore?.Dispose();
		_disposed = true;

		GC.SuppressFinalize(this);
	}

	~BassAudioPlayer()
	{
		Dispose();
	}

	public static void DisplayOutputs()
	{
		Console.WriteLine($"BASS output devices ({Bass.DeviceCount})");

		for (var i = 0; i < Bass.DeviceCount; i++ ) {
			var dev = Bass.GetDeviceInfo(i);
			Console.WriteLine($"\t{i} [{dev.Name}] - driver: {dev.Driver} type: {dev.Type} default: {dev.IsDefault}");
		}
	}
}
