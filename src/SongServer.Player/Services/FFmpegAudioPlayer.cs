using Microsoft.Extensions.Options;
using SongServer.Audio;
using SongServer.Audio.FFmpeg;
using SongServer.Audio.Sdl;
using SongServer.Configuration;
using SongServer.Services.Library;

namespace SongServer.Services;

internal class FFmpegAudioPlayer : AudioPlayer, IDisposable
{
	private const int _minPositionChangeMs = 100;

	private readonly ILogger _logger;
	private readonly ILoggerFactory _loggerFactory;
	private readonly SdlAudioDevice _device;
	private readonly PcmAudioSpec _outputSpec;
	private readonly LibraryService _library;
	private readonly Timer _progressTimer;
	private readonly Lock _stateLock = new();
	private bool _disposed = false;
	private Task _playTask = Task.CompletedTask;
	private CancellationTokenSource? _playCts;
	private Stream? _inputStream;
	private SdlAudioOutputStream? _outputStream;
	private AudioConverter? _converter;

	public FFmpegAudioPlayer(FileInformationRetriever retriever, LibraryService library, IOptions<PlayerOptions> options, ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<FFmpegAudioPlayer>(), retriever)
	{
		_loggerFactory = loggerFactory;
		_logger        = loggerFactory.CreateLogger<FFmpegAudioPlayer>();
		_library       = library;
		_progressTimer = new(UpdateProgress, null, Timeout.Infinite, Timeout.Infinite);
		_device        = new(gain: (float)options.Value.Gain);
		_outputSpec    = new(
			_device.AudioSpec.Format.AsFormat(),
			_device.AudioSpec.Channels,
			_device.AudioSpec.Freq
		);
	}

	~FFmpegAudioPlayer() => Dispose(disposing: false);

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	public override Task<bool> Pause()
	{
		var ret = _device.Pause();

		if (ret) {
			_logger.LogInformation("SDL audio device paused");

			// stop the progress update timer
			_progressTimer.Change(Timeout.Infinite, Timeout.Infinite);

			// let the world know we're paused
			OnPlayerStateChanged(State.Paused);
		} else {
			_logger.LogWarning("Pause was not successful.");
		}

		return Task.FromResult(ret);
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

		OnPlayerStateChanged(State.Stopped);

		return true;
	}

	protected override Task<bool> Resume()
	{
		// if we aren't playing, we can't resume
		if (_playCts == null) {
			return Task.FromResult(false);
		}

		var ret = _device.Resume();

		if (ret) {
			OnPlayerStateChanged(State.Playing);

			// start the progress update timer
			_progressTimer.Change(_minPositionChangeMs, _minPositionChangeMs);
		}

		return Task.FromResult(ret);
	}

	protected override async Task<bool> Play(SongReference song)
	{
		if (_playCts != null) {
			// we're already playing... stop so we can play a new song
			await Stop();
		}

		var dbSong = await _library.GetSong(song) ?? throw new InvalidOperationException("The specified song does not exist.");

		// get the stream that'll read the audio data
		//var inputStream = Stream.Null;
		//_inputStream = File.OpenRead(@"\\vmhost.local\media\Audio\Soundtracks\Descent II\02-Title.opus");
		_inputStream = GetStream(dbSong);

		// make a new AudioConverter
		_converter = new AudioConverter(_inputStream, _loggerFactory.CreateLogger<AudioConverter>());

		// make an output stream
		_outputStream = new SdlAudioOutputStream(_device.AudioSpec, _device, _loggerFactory.CreateLogger<SdlAudioOutputStream>());

		// make a new cancellation token source that'll let us stop
		_playCts = new CancellationTokenSource();

		_logger.LogDebug("Starting new task for playback for song {relativePath}", song.RelativePath);

		// notify the system we're beginning playback
		OnPlayerStateChanged(State.Playing, new SongMetadata(dbSong.Performers, dbSong.Album, dbSong.Title, dbSong.TrackNumber, dbSong.TrackCount, null, dbSong.Duration));

		// start up the playback task
		_playTask = Task.Factory
			.StartNew(ExecutePlayback, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent)
			.ContinueWith(CleanupPlayback);

		// start the timer that'll report playback progress
		_progressTimer.Change(_minPositionChangeMs, _minPositionChangeMs);

		return true;
	}

	private void ExecutePlayback()
	{
		if (_playCts == null || _converter == null || _outputStream == null) {
			throw new InvalidOperationException("CancellationTokenSource has not been initialized.");
		}

		// our job here is to play the song to completion
		// we'll be running in a task, and we stop if our
		// token is cancelled (_playCts)

		// register a callback for when the token gets signalled
		using var r = _playCts.Token.Register(() => {
			_device.Pause();
			_outputStream.Clear();
		});

		// run the data through the converter and into the output stream
		// note that this method probably spends all its time in Write() and won't respond to
		// token cancellation; we'd need to cancel the token, clear the buffered data from the
		// SDL stream, then this method would notice the cancellation (after writing some more)
		// and only THEN we could clear the data for good
		_converter.ConvertStream(_outputStream, _outputSpec, _playCts.Token);

		if (_playCts.IsCancellationRequested) {
			// we stopped because we were cancelled, so we're paused; fix that
			_outputStream.Clear();
			_device.Resume();
		} else {
			// wait for the output stream to be done
			_outputStream.WaitForFinish(_playCts.Token);
		}
	}

	private void CleanupPlayback(Task playbackTask)
	{
		if (playbackTask.IsFaulted) {
			_logger.LogError(playbackTask.Exception, "Error during stream conversion");
		}

		var isCancelled = _playCts?.IsCancellationRequested ?? true;

		lock (_stateLock) {
			// clean up everything
			// stop the progress update timer
			_progressTimer.Change(Timeout.Infinite, Timeout.Infinite);

			// dispose the output stream
			try {
				_outputStream?.Dispose();
			} catch {
				// do nothing
			}

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

			_outputStream  = null;
			_converter     = null;
			_playCts       = null;
			_inputStream   = null;
		}

		// fire off the notification that playback is finished, but only if we weren't told to stop
		if (!isCancelled) {
			_ = OnPlaybackFinished();
		}
	}

	private void UpdateProgress(object? state)
	{
		lock (_stateLock) {
			// if we don't have an output stream, then we (obv) can't report progress of it
			if (_outputStream == null) {
				return;
			}

			try {
				OnPositionChanged(_outputStream.PlayedDuration);
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

		_device.Dispose();

		_disposed = true;
	}
}
