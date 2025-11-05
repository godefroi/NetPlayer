using Microsoft.Extensions.Logging;
using SDL3;

namespace SongServer.Audio.Sdl;

public class SdlAudioOutputStream : Stream
{
	private const int _bufferLowWaterMark = 1024 * 1024 * 1; // 1MB
	private const int _waitDelayMs = 50;

	private readonly SDL.AudioSpec _inputSpec;
	private readonly SdlAudioDevice _device;
	private readonly ILogger _logger;
	private readonly nint _streamId = nint.Zero;
	private volatile int _writtenCount = 0;

	public override bool CanRead => false;

	public override bool CanSeek => false;

	public override bool CanWrite => true;

	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => _writtenCount;
		set => throw new NotSupportedException();
	}

	public TimeSpan PlayedDuration => CalculatePlayedDuration();

	public SdlAudioOutputStream(SDL.AudioSpec inputSpec, SdlAudioDevice device, ILogger<SdlAudioOutputStream> logger)
	{
		_inputSpec = inputSpec;
		_device    = device;
		_logger    = logger;
		_streamId  = SDL.CreateAudioStream(_inputSpec, _device.AudioSpec);

		if (_streamId == nint.Zero) {
			throw new InvalidOperationException($"Unable to initialize SDL audio stream: {SDL.GetError()}");
		}

		if (!SDL.BindAudioStream(_device.DeviceId, _streamId)) {
			var err = SDL.GetError();
			SDL.DestroyAudioStream(_streamId);
			_streamId = nint.Zero;
			throw new Exception($"Unable to bind audio stream to device: {err}");
		}

		_logger.LogInformation(
			"Stream opened with spec: [{sfreq} kHz {schan} chan {sform}] to device with spec: [{dfreq} kHz {dchan} chan {dform}]",
			_inputSpec.Freq, _inputSpec.Channels, _inputSpec.Format, _device.AudioSpec.Freq, _device.AudioSpec.Channels, _device.AudioSpec.Format);
	}

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count)
	{
		// wait until there's less than our low water mark in the buffer
		while (true) {
			var queued = SDL.GetAudioStreamQueued(_streamId);

			if (queued < _bufferLowWaterMark) {
				break;
			}

			SDL.Delay(100);
		}

		if (!SDL.PutAudioStreamData(_streamId, buffer, count)) {
			_logger.LogError("Unable to write audio data to stream: {err}", SDL.GetError());
		}

		Interlocked.Add(ref _writtenCount, count);
	}

	public override void Flush()
	{
		if (!SDL.FlushAudioStream(_streamId)) {
			_logger.LogError("Unable to flush audio stream: {err}", SDL.GetError());
		}
	}

	public bool Clear() => SDL.ClearAudioStream(_streamId);

	public void WaitForFinish(CancellationToken cancellationToken = default)
	{
		while (SDL.GetAudioStreamAvailable(_streamId) > 0 && !cancellationToken.IsCancellationRequested) {
			var queued = SDL.GetAudioStreamQueued(_streamId);

			if (queued == -1) {
				_logger.LogError("Unable to retrieve queued stream data size: {err}", SDL.GetError());
			}

			SDL.Delay(_waitDelayMs);
		}
	}

	public async Task WaitForFinishAsync(CancellationToken cancellationToken = default)
	{
		while (SDL.GetAudioStreamAvailable(_streamId) > 0 && !cancellationToken.IsCancellationRequested) {
			var queued = SDL.GetAudioStreamQueued(_streamId);

			if (queued == -1) {
				_logger.LogError("Unable to retrieve queued stream data size: {err}", SDL.GetError());
			}

			await Task.Delay(_waitDelayMs, cancellationToken);
		}
	}

	private TimeSpan CalculatePlayedDuration()
	{
		var bytesPerSample = _inputSpec.Format switch {
			SDL.AudioFormat.AudioS16LE => 2,
			SDL.AudioFormat.AudioS16BE => 2,
			SDL.AudioFormat.AudioF32LE => 4,
			SDL.AudioFormat.AudioF32BE => 4,
			SDL.AudioFormat.AudioS32BE => 4,
			_ => throw new NotImplementedException($"The audio format {_inputSpec.Format} has not yet been implemented for time calculation."),
		};
		var bytesPerFrame  = _inputSpec.Channels * bytesPerSample;
		var bytesPerSecond = bytesPerFrame * _inputSpec.Freq;
		var queuedBytes    = SDL.GetAudioStreamQueued(_streamId);
		var consumedBytes  = _writtenCount - queuedBytes;
		var positionSecs   = (double)consumedBytes / bytesPerSecond;

		return TimeSpan.FromSeconds(positionSecs);
	}
}
