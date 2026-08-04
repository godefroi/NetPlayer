using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SongServer.Audio;
using SongServer.Audio.FFmpeg;
using SongServer.Configuration;
using SongServer.Services.Library;

namespace SongServer.Services;

internal class NetPlayerAudioPlayer : AudioPlayer, IDisposable {
	private const int _minPositionChangeMs = 100;
	private const int _bitsPerSample = 32;
	private const int _channelCount = 2;
	private const int _sampleRate = 48000;
	private const int _port = 8888;

	private readonly ILogger _logger;
	private readonly PcmAudioSpec _outputSpec;
	private readonly LibraryService _library;
	private readonly Timer _progressTimer;
	private readonly Lock _stateLock = new();
	private readonly string _device;
	private readonly AudioConverterFactory _factory;
	private bool _disposed = false;
	private Task _playTask = Task.CompletedTask;
	private CancellationTokenSource? _playCts;
	private Stream? _inputStream;
	private PauseableStream<NetworkStream>? _outputStream;
	private AudioConverter? _converter;
	private long _songStartBytes = 0L;

	public NetPlayerAudioPlayer(FileInformationRetriever retriever, LibraryService library, AudioConverterFactory converterFactory, IOptions<PlayerOptions> options, ILogger<NetPlayerAudioPlayer> logger) : base(logger, retriever)
	{
		_logger = logger;
		_library = library;
		_progressTimer = new(UpdateProgress, null, Timeout.Infinite, Timeout.Infinite);
		_outputSpec = new(_bitsPerSample == 32 ? SampleFormat.S32LE : throw new NotSupportedException("Only 32-bit data is supported."), _channelCount, _sampleRate);
		_device = options.Value.Device;
		_factory = converterFactory;

		_logger.LogInformation("Configured to connect to device at {device}", _device);
	}

	~NetPlayerAudioPlayer() => Dispose(disposing: false);

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	public override Task<bool> Pause()
	{
		// if we aren't playing, we can't pause
		if (_playCts == null || _outputStream == null) {
			return Task.FromResult(false);
		}

		_outputStream.Pause();

		// stop the progress update timer
		_progressTimer.Change(Timeout.Infinite, Timeout.Infinite);

		// let the world know we're paused
		OnPlayerStateChanged(State.Paused);

		return Task.FromResult(true);
	}

	public override async Task<bool> Stop()
	{
		if (_playCts == null) {
			return true;
		}

		// TODO: this seems dangerously not thread-safe
		_playCts.Cancel();
		await _playTask;
		_playTask.Dispose();
		_playTask = Task.CompletedTask;

		// We're stopping, so dispose the output stream
		try {
			_outputStream?.UnderlyingStream.Close(TimeSpan.FromSeconds(2));
			_outputStream?.Dispose();
		} catch {
			// do nothing
		} finally {
			_outputStream = null;
		}

		OnPlayerStateChanged(State.Stopped);

		return true;
	}

	protected override Task<bool> Resume()
	{
		// if we aren't playing, we can't resume
		if (_playCts == null || _outputStream == null) {
			return Task.FromResult(false);
		}

		_outputStream.Resume();

		OnPlayerStateChanged(State.Playing);

		return Task.FromResult(true);
	}

	protected override async Task<bool> Play(SongReference song)
	{
		if (_playCts != null) {
			// we're already playing... stop so we can play a new song
			// note that this code is duplicated from Stop()... that's not ideal
			_playCts.Cancel();
			await _playTask;
			_playTask.Dispose();
			_playTask = Task.CompletedTask;
		}

		var dbSong = await _library.GetSong(song) ?? throw new InvalidOperationException($"The specified song ({song.RelativePath}) does not exist in source {song.SourceId}.");

		// get the stream that'll read the audio data
		_inputStream = GetStream(dbSong);

		// make a new AudioConverter
		_converter = _factory.CreateConverter(_inputStream);

		// make an output stream
		if (_outputStream == null) {
			_logger.LogInformation("Connecting to remote device at {device} port {port}", _device, _port);
			// connect to the netplayer, make a stream
			var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
			using var timeoutToken = new CancellationTokenSource(15000);
			await s.ConnectAsync(_device, _port, timeoutToken.Token);

			if (s.Connected) {
				_outputStream = new(new NetworkStream(s, true));
				_logger.LogInformation("Connected to remote device.");
			} else {
				_logger.LogError("Unable to connect to remote device.");
				throw new Exception("Unable to connect to remote device.");
			}
		}

		// make a new cancellation token source that'll let us stop
		_playCts = new CancellationTokenSource();

		_logger.LogDebug("Starting new task for playback for song {relativePath}", song.RelativePath);

		// notify the system we're beginning playback
		OnPlayerStateChanged(State.Playing, new SongMetadata(dbSong.Performers, dbSong.Album, dbSong.Title, dbSong.TrackNumber, dbSong.TrackCount, null, dbSong.Duration));

		// start up the playback task
		_playTask = Task.Factory.StartNew(ExecutePlayback, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);

		// start the timer that'll report playback progress
		_progressTimer.Change(_minPositionChangeMs, _minPositionChangeMs);

		return true;
	}

	private void ExecutePlayback()
	{
		var canceled = false;

		// our job here is to play the song to completion
		// we'll be running in a task, and we stop if our token is cancelled (_playCts)

		if (_playCts == null || _converter == null || _outputStream == null) {
			throw new InvalidOperationException("CancellationTokenSource has not been initialized.");
		}

		try {
			// save where the output stream was when we started the song, so we can
			// (crudely) track our position in it
			_songStartBytes = _outputStream.WrittenBytes;

			// run the data through the converter and into the output stream
			// note that this method probably spends all its time in Write() and won't respond to
			// token cancellation; we'd need to cancel the token, clear the buffered data from the
			// stream, then this method would notice the cancellation (after writing some more)
			// and only THEN we could clear the data for good
			_converter.ConvertStream(_outputStream, _outputSpec, _playCts.Token);
		} catch (TaskCanceledException) {
			_logger.LogDebug("Playback task cancelled");

			// we don't want to do any futher processing; Stop() was called, so we'll
			// let them handle whatever cleanup needs to happen.
			canceled = true;
		} catch (Exception e) {
			_logger.LogError(e, "Error during stream conversion");
		}

		// double-check for cancellation just in case we didn't get an exception
		canceled |= _playCts?.IsCancellationRequested ?? true;

		lock (_stateLock) {
			// clean up everything
			// stop the progress update timer
			_progressTimer.Change(Timeout.Infinite, Timeout.Infinite);

			// dispose the converter
			try {
				_converter?.Dispose();
			} catch {
				// do nothing
			}

			// dispose the cts
			try {
				_playCts?.Dispose();
			} catch {
				// do nothing
			}

			// dispose the input stream
			try {
				_inputStream?.Dispose();
			} catch {
				// do nothing
			}

			_converter = null;
			_playCts = null;
			_inputStream = null;

			if (!canceled) {
				// fire off the notification that playback is finished
				_ = OnPlaybackFinished();
			}
		}
	}

	private void UpdateProgress(object? state)
	{
		lock (_stateLock) {
			// if we don't have an output stream, then we (obv) can't report progress of it
			if (_outputStream == null) {
				return;
			}

			// writtenBytes is the number of bytes written to the stream since
			// the song started (unless, of course, something went wrong and we
			// had to reconnect the stream)
			var writtenBytes = _outputStream.WrittenBytes - _songStartBytes;

			const int bytesPerSample = _bitsPerSample / 8;
			const int bytesPerFrame = bytesPerSample * _channelCount;
			const long bytesPerSecond = _sampleRate * bytesPerFrame;

			// our position in the song can be calculated like this:
			var positionSeconds = (double)writtenBytes / bytesPerSecond;

			try {
				OnPositionChanged(TimeSpan.FromSeconds(positionSeconds));
			} catch {
				// ignore all errors, because there's nothing we can do about it
			}
		}
	}

	protected virtual void Dispose(bool disposing)
	{
		if (_disposed) {
			return;
		}

		if (disposing) {
			// TODO: dispose managed state (managed objects)
			_progressTimer.Dispose();
		}

		_disposed = true;
	}
}
