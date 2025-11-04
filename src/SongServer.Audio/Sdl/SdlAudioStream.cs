using System.Buffers;
using Microsoft.Extensions.Logging;
using SDL3;

namespace SongServer.Audio.Sdl;
public sealed class SdlAudioStream : IDisposable
{
	private const int _positionReportMinChangeMs = 100;
	private const int _bufferLowWaterMark = 1024 * 1024 * 1; // 1MB
	private const int _bufferHighWaterMark = 1024 * 1024 * 2; // 2MB
	private const int _streamReadBufferSize = 1024 * 128; // 128kb
	private readonly SDL.AudioSpec _inputSpec;
	private readonly SdlAudioDevice _device;
	private readonly ILogger _logger;
	private nint _streamId = nint.Zero;
	private bool _disposed;
	private TimeSpan _lastPosition = TimeSpan.FromDays(-1); // minvalue can cause overflow
	private volatile int _writtenCount = 0;

	public SdlAudioStream(SDL.AudioSpec inputSpec, SdlAudioDevice device, ILogger<SdlAudioStream> logger)
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

	public event EventHandler<ProgressEventArgs>? Progress;

	public TimeSpan Position { get; private set; }

	~SdlAudioStream() => Dispose(false);

	public async Task WritePcmAudio(Stream pcmStream, CancellationToken cancellationToken = default)
	{
		try {
			while (!cancellationToken.IsCancellationRequested) {
				// wait until there's less than our low water mark in the buffer
				while (!cancellationToken.IsCancellationRequested) {
					var queued   = SDL.GetAudioStreamQueued(_streamId);
					var consumed = _writtenCount - queued;

					// report our progress through the stream
					OnProgress(consumed);

					if (queued < _bufferLowWaterMark) {
						break;
					}

					await Task.Delay(_positionReportMinChangeMs, cancellationToken);
				}

				if (!await FillBuffer(pcmStream, cancellationToken)) {
					break;	
				}
			}

			if (!SDL.FlushAudioStream(_streamId)) {
				_logger.LogError("Unable to flush audio stream: {err}", SDL.GetError());
			}

			while (SDL.GetAudioStreamAvailable(_streamId) > 0 && !cancellationToken.IsCancellationRequested) {
				var queued = SDL.GetAudioStreamQueued(_streamId);

				if (queued == -1) {
					_logger.LogError("Unable to retrieve queued stream data size: {err}", SDL.GetError());
				} else {
					// report our progress through the stream
					OnProgress(_writtenCount - queued);
				}

				await Task.Delay(_positionReportMinChangeMs, cancellationToken);
			}
		} catch (TaskCanceledException) {
			// we aren't worried about TCE
		}
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (_disposed) {
			return;
		}

		if (disposing) {
			// TODO: dispose managed state (managed objects)
		}

		if (_streamId != nint.Zero) {
			SDL.DestroyAudioStream(_streamId);
			_streamId = nint.Zero;
		}

		_disposed = true;
	}

	private async Task<bool> FillBuffer(Stream source, CancellationToken cancellationToken = default)
	{
		var buf = ArrayPool<byte>.Shared.Rent(_streamReadBufferSize);

		try {
			var queued = SDL.GetAudioStreamQueued(_streamId);

			if (queued == -1) {
				_logger.LogError("Unable to retrieve queued stream data size: {err}", SDL.GetError());
				return true;
			}

			while (queued < _bufferHighWaterMark && !cancellationToken.IsCancellationRequested) {
				var cnt = await source.ReadAsync(buf, cancellationToken);

				if (cnt == 0) {
					return false;
				}

				if (!SDL.PutAudioStreamData(_streamId, buf, cnt)) {
					_logger.LogError("Unable to write audio data to stream: {err}", SDL.GetError());
					return true;
				}

				queued = SDL.GetAudioStreamQueued(_streamId);

				if (queued == -1) {
					_logger.LogError("Unable to retrieve queued stream data size: {err}", SDL.GetError());
					return true;
				}

				Interlocked.Add(ref _writtenCount, cnt);

				// report our progress through the stream
				OnProgress(_writtenCount - queued);
			}

			return !cancellationToken.IsCancellationRequested;
		} catch (TaskCanceledException) {
			// if we were cancelled just leave
			return false;
		} finally {
			ArrayPool<byte>.Shared.Return(buf);
		}
	}

	private void OnProgress(long consumedBytes)
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
		var positionSecs   = (double)consumedBytes / bytesPerSecond;

		Position = TimeSpan.FromSeconds(positionSecs);

		if (Math.Abs((Position - _lastPosition).TotalMilliseconds) > _positionReportMinChangeMs) {
			_lastPosition = Position;
			_ = Task.Run(() => {
				try {
					Progress?.Invoke(this, new ProgressEventArgs() { Position = Position });
				} catch (Exception e) {
					_logger.LogError(e, "Error while handling Progress event");
				}
			});
		}
	}
}
