#if false
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SongServer.Services.Mpd;

namespace SongServer.Services;

internal class MpdAudioPlayer(MpdClient mpdClient, ILogger<MpdAudioPlayer> logger) : AudioPlayer(logger), IDisposable
{
	private readonly ILogger _logger = logger;
	private readonly CancellationTokenSource _cancellationTokenSource = new();
	private readonly MpdClient _mpd = mpdClient;
	private readonly SemaphoreSlim _semaphore = new(1, 1);
	private Task? _statusTask;
	private CancellationTokenSource? _statusToken;
	private Process? _mpdProcess;
	private bool _disposed = false;

	public async Task InitializePlayer(CancellationToken cancellationToken = default)
	{
		try {
			// Start the MPD process
			//StartMpdProcess();

			// connect the mpd client
			await _mpd.Connect(cancellationToken: cancellationToken);

			await _mpd.SendCommand("consume 1", cancellationToken);
			await _mpd.SendCommand("random 0", cancellationToken);
			await _mpd.SendCommand("repeat 0", cancellationToken);
			await _mpd.SendCommand("single 1", cancellationToken);
			await _mpd.SendCommand("clear", cancellationToken);
			await _mpd.SendCommand("stop", cancellationToken);
			await _mpd.SendCommand("setvol 25", cancellationToken);
			await _mpd.SendCommand("replay_gain_mode auto", cancellationToken);

			// we expect things to have changed here (playlist, mixer, options at least,
			// and player if it had been previously playing for some reason)
			await _mpd.GetChangedSubsystems(cancellationToken);

			OnPlayerStateChanged(State.Stopped);
		} catch (Exception ex) {
			_logger.LogError(ex, "Failed to start MPD player");
			Dispose();
			throw;
		}
	}

	public override async Task<bool> Pause()
	{
		await _semaphore.WaitAsync();

		try {
			var resp = await _mpd.SendCommand("pause 1");

			if (resp.Error != null) {
				_logger.LogError("MPD error pausing playback: {message}", resp.Error.Message);
				return false;
			} else {
				return true;
			}
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
			var resp = await _mpd.SendCommand("stop");

			if (resp.Error != null) {
				_logger.LogError("MPD error stopping playback: {message}", resp.Error.Message);
				return false;
			} else {
				return true;
			}
		} catch (Exception ex) {
			_logger.LogError(ex, "Error stopping playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	protected override async Task<bool> Play(SongReference song)
	{
		var streamUri = new UriBuilder(Uri.UriSchemeHttp, "localhost", 5000, $"/library/stream/{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(song)))}").Uri;

		_logger.LogInformation("Loading file: {path}", streamUri);

		await _semaphore.WaitAsync();

		try {
			// stop any currently playing media first
			await _mpd.SendCommand("stop");

			// clear the current playlist
			await _mpd.SendCommand("clear");

			// add the file to the playlist
			await _mpd.SendCommand($"add \"{streamUri}\"");

			// play it
			await _mpd.SendCommand("play");

			_ = Task.Run(async () => {
				var subs = await _mpd.GetChangedSubsystems();
				foreach (var s in subs) {
					Console.WriteLine($"Changed: {s}");
				}
			});

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
			var resp = await _mpd.SendCommand("pause 0");

			if (resp.Error != null) {
				_logger.LogError("MPD error resuming playback: {message}", resp.Error.Message);
				return false;
			} else {
				return true;
			}
		} catch (Exception ex) {
			_logger.LogError(ex, "Error resuming playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	private void StartMpdProcess()
	{
		var psi = new ProcessStartInfo {
			FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mpd.exe" : "mpd",
			Arguments = $"--no-daemon --no-config",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};

		_logger.LogInformation("Starting MPD process");

		try {
			_mpdProcess = Process.Start(psi);

			if (_mpdProcess == null) {
				throw new InvalidOperationException("Failed to start MPD process");
			}

			// Log MPD output
			_ = Task.Run(async () => {
				try {
					var line = await _mpdProcess.StandardOutput.ReadLineAsync();
					while (line != null) {
						_logger.LogInformation("MPD-out: {line}", line);
						line = await _mpdProcess.StandardOutput.ReadLineAsync();
					}
				} catch (Exception ex) {
					_logger.LogError(ex, "Error reading MPD stdout");
				}
			});

			_ = Task.Run(async () => {
				try {
					var line = await _mpdProcess.StandardError.ReadLineAsync();
					while (line != null) {
						_logger.LogError("MPD-err: {line}", line);
						line = await _mpdProcess.StandardError.ReadLineAsync();
					}
				} catch (Exception ex) {
					_logger.LogError(ex, "Error reading MPD stderr");
				}
			});

			// Give MPD a moment to start
			Thread.Sleep(500);
		} catch (Exception ex) {
			_logger.LogError(ex, "Failed to start MPD process");
			throw;
		}
	}

	private void HandlePositionChange(string time)
	{
		// MPD returns time as "current:total"
		if (time.Contains(":")) {
			var parts = time.Split(':');
			if (int.TryParse(parts[0], out int current)) {
				OnPositionChanged(TimeSpan.FromSeconds(current));
			}
		}
	}

	private async Task ReadStatus()
	{
		while (_statusToken == null || !_statusToken.IsCancellationRequested) {
			// read the status

			// raise an event (or whatever)

			// wait a bit
		}
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		_cancellationTokenSource?.Cancel();
		_mpdProcess?.Dispose();
		_mpdProcess = null;
		_cancellationTokenSource?.Dispose();
		_semaphore?.Dispose();
		_disposed = true;

		GC.SuppressFinalize(this);
	}

	~MpdAudioPlayer()
	{
		Dispose();
	}
}
#endif
