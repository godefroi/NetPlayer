using ManagedBass;
using ManagedBass.Opus;
using SongServer.Services.Library;

namespace SongServer.Services;

internal class BassAudioPlayer : AudioPlayer, IDisposable
{
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _semaphore = new(1, 1);
	private readonly LibraryService _library;
	private readonly CancellationTokenSource _metadataToken = new();
	private int _stream = 0;
	private TimeSpan _lastPos = TimeSpan.Zero;
	private PlaybackState _lastState = (PlaybackState)(-1);
	private Task _statusTask = Task.CompletedTask;
	private bool _disposed = false;

	public BassAudioPlayer(ILogger<BassAudioPlayer> logger, LibraryService library) : base(logger)
	{
		_logger  = logger;
		_library = library;

		try {
			// Initialize BASS with the specified device (or default)
			if (!Bass.Init()) {
				throw new InvalidOperationException($"Failed to initialize BASS: {Bass.LastError}");
			}

			Bass.Configure(ManagedBass.Configuration.GlobalStreamVolume, 4500);

			_statusTask = MonitorPlayback();

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

			var dbSong = await _library.GetSong(song) ?? throw new InvalidOperationException("The specified song does not exist.");
			var songPath = Path.Combine(dbSong.SourcePath, dbSong.RelativePath);

			// Create a BASS stream from the file path
			_stream = Path.GetExtension(songPath) switch {
				".opus" => BassOpus.CreateStream(songPath, 0, 0, BassFlags.Default | BassFlags.BassLoudnessCurrent),
				_ => Bass.CreateStream(songPath, 0, 0, BassFlags.Default | BassFlags.BassLoudnessCurrent),
			};

			if (_stream == 0) {
				_logger.LogError("Failed to create stream: {error}", Bass.LastError);
				return false;
			}

			// Load metadata from the file
			//await LoadMetadata(song.RelativePath);

			// Start playback
			if (!Bass.ChannelPlay(_stream)) {
				_logger.LogError("Failed to play stream: {error}", Bass.LastError);
				Bass.StreamFree(_stream);
				_stream = 0;
				return false;
			}

			// Start position monitoring
			//_metadataToken = new CancellationTokenSource();
			//_ = Task.Run(() => MonitorPlayback(_metadataToken.Token));

			OnPlayerStateChanged(State.Playing);
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error playing file");
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

	private async Task LoadMetadata(string filePath)
	{
		try {
			var file = TagLib.File.Create(filePath);

			var artists = file.Tag.Performers.Length > 0
				? file.Tag.Performers.ToList()
				: null;

			var metadata = new SongMetadata(
				Artists: artists,
				Album: file.Tag.Album,
				Title: file.Tag.Title,
				TrackNumber: (int?)file.Tag.Track,
				TrackTotal: (int?)file.Tag.TrackCount,
				ArtworkUrl: null,
				Duration: file.Properties.Duration
			);

			OnMediaMetadataAvailable(metadata);

			await Task.CompletedTask;
		} catch (Exception ex) {
			_logger.LogError(ex, "Failed to load metadata from file: {file}", filePath);
		}
	}

	private async Task MonitorPlayback()
	{
		try {
			while (!_metadataToken.IsCancellationRequested) {
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

						var playbackState = Bass.ChannelIsActive(_stream);

						if (playbackState != _lastState) {
							_logger.LogInformation("State transitioned {old} -> {new}", _lastState, playbackState);
							_lastState = playbackState;
						}
						// we probably don't have to track the last state; if we have a channel
						// and we're stopped, then playback finished... we can track all other
						// state changes in their respective methods
						if (playbackState == PlaybackState.Stopped) {
							// if we got here, then the song finished (if Stop() had been called,
							// we wouldn't have a stream anymore)

							// tear down any BASS resources we've got tied up
							Bass.StreamFree(_stream);
							_stream = 0;

							// notify the base class we're ready to advance (on a new
							// thread, because we're holding the semaphore, and the
							// base class is surely going to call Play() on us)
							_ = OnPlaybackFinished();
							break;
						}
					} else {
						// ensure we zero out position and state if _stream is recently zeroed out
					}
				} finally {
					_semaphore.Release();
				}

				await Task.Delay(250, _metadataToken.Token);
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

		_metadataToken?.Cancel();
		_metadataToken?.Dispose();

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
}
