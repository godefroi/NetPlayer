using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SongServer.Services;

internal partial class FfplayAudioPlayer(ILogger<FfplayAudioPlayer> logger) : AudioPlayer(logger), IDisposable
{
	private ILogger _logger = logger;
	private Process? _currentProcess;
	private CancellationTokenSource? _stdoutCts;
	private CancellationTokenSource? _stderrCts;
	private Task? _stdoutTask;
	private Task? _stderrTask;
	private string? _tempMedia;
	private bool _disposed = false;

	~FfplayAudioPlayer() => Dispose(false);

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (!_disposed) {
			if (disposing) {
				StopProcess();
				_stdoutCts?.Dispose();
				_stderrCts?.Dispose();
			}
			_disposed = true;
		}
	}

	public override void Pause()
	{
		lock (_stateLock) {
			if (_currentProcess == null || _currentProcess.HasExited) {
				_logger.LogWarning("Cannot pause: no active process");
				return;
			}

			try {
				// Send 'p' command to ffplay for pause
				_currentProcess.StandardInput.Write("p");
				_currentProcess.StandardInput.Flush();
				OnPlayerStateChanged(State.Paused);
			} catch (Exception ex) {
				_logger.LogError(ex, "Error pausing playback");
			}
		}
	}

	public override void Play()
	{
		_logger.LogInformation("Play() called. State: {state}", _currentState);

		lock (_stateLock) {
			// If we have an active process and it's paused, resume it
			if (_currentProcess != null && !_currentProcess.HasExited && _currentState == State.Paused) {
				try {
					_currentProcess.StandardInput.Write("p");
					_currentProcess.StandardInput.Flush();
					OnPlayerStateChanged(State.Playing);
					return;
				} catch (Exception ex) {
					_logger.LogError(ex, "Error resuming playback");
				}
			}
		}

		// Otherwise, advance to the next song
		Advance();
	}

	public override void Stop()
	{
		lock (_stateLock) {
			StopProcess();
			OnPlayerStateChanged(State.Stopped);
		}
	}

	protected override void Play(Uri uri)
	{
		_logger.LogInformation("Play requested for {Uri}", uri);

		lock (_stateLock) {
			// Stop any existing process
			StopProcess();

			// Extract metadata from the file
			ExtractMetadata(uri);

			try {
				// Copy the source file to a temporary location
				_tempMedia = Path.Combine(Path.GetTempPath(), $"ffplay_{Guid.NewGuid()}{Path.GetExtension(uri.LocalPath)}");
				System.IO.File.Copy(uri.LocalPath, _tempMedia, overwrite: false);
				_logger.LogDebug("Copied audio file to temp location: {tempPath}", _tempMedia);
			} catch (Exception ex) {
				_logger.LogError(ex, "Error copying file to temporary location");
				return;
			}

			// Start a new ffplay process
			var regex = StatusLineRegex();

			_stdoutCts = new CancellationTokenSource();
			_stderrCts = new CancellationTokenSource();

			_currentProcess = new Process() {
				StartInfo = new() {
					FileName = "ffplay",
					Arguments = $"-autoexit -nodisp -vn -hide_banner -loglevel warning -stats \"{_tempMedia}\"",
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
				}
			};

			_currentProcess.Start();

			// Start tasks to read stdout and stderr asynchronously
			_stdoutTask = Task.Run(() => ReadStdout(_stdoutCts.Token));
			_stderrTask = Task.Run(() => ReadStderr(_stderrCts.Token));

			OnPlayerStateChanged(State.Playing);
		}
	}

	private void StopProcess()
	{
		// Must be called within _stateLock
		if (_currentProcess != null && !_currentProcess.HasExited) {
			try {
				_stdoutCts?.Cancel();
				_stderrCts?.Cancel();

				_currentProcess.StandardInput.Close();
				_currentProcess.WaitForExit(5000);

				if (!_currentProcess.HasExited) {
					_currentProcess.Kill();
					_currentProcess.WaitForExit(1000);
				}
			} catch (Exception ex) {
				_logger.LogError(ex, "Error stopping ffplay process");
			} finally {
				_currentProcess?.Dispose();
				_currentProcess = null;
			}
		}

		// Clean up the temporary file
		if (!string.IsNullOrEmpty(_tempMedia)) {
			try {
				if (System.IO.File.Exists(_tempMedia)) {
					System.IO.File.Delete(_tempMedia);
					_logger.LogDebug("Deleted temporary file: {tempPath}", _tempMedia);
				}
			} catch (Exception ex) {
				_logger.LogError(ex, "Error deleting temporary file {tempPath}", _tempMedia);
			} finally {
				_tempMedia = null;
			}
		}
	}

	private void ReadStdout(CancellationToken ct)
	{
		try {
			string? line;
			while (!ct.IsCancellationRequested && _currentProcess != null && (line = _currentProcess.StandardOutput.ReadLine()) != null) {
				_logger.LogDebug("[STDOUT] {line}", line);
			}
		} catch (Exception ex) {
			if (!ct.IsCancellationRequested) {
				_logger.LogError(ex, "Error reading stdout");
			}
		}
	}

	private void ReadStderr(CancellationToken ct)
	{
		var regex = StatusLineRegex();

		try {
			string? line;
			while (!ct.IsCancellationRequested && _currentProcess != null && (line = _currentProcess.StandardError.ReadLine()) != null) {
				if (TryParseStatus(line, regex, out var pos, out var sync, out var aq)) {
					OnPositionChanged(pos);
					_logger.LogDebug("Playback position: {pos}s, sync: {sync}ms, aq: {aq}KB", pos.TotalSeconds, sync, aq);
				} else {
					_logger.LogDebug("[STDERR] {line}", line);
				}
			}
		} catch (Exception ex) {
			if (!ct.IsCancellationRequested) {
				_logger.LogError(ex, "Error reading stderr");
			}
		}
	}

	private void ExtractMetadata(Uri uri)
	{
		try {
			var file = TagLib.File.Create(uri.LocalPath);
			var tag = file.Tag;

			var artists = new List<string>();
			if (!string.IsNullOrWhiteSpace(tag.FirstPerformer)) {
				artists.Add(tag.FirstPerformer);
			}
			if (tag.AlbumArtists?.Length > 0 && !artists.Contains(tag.FirstAlbumArtist)) {
				artists.Add(tag.FirstAlbumArtist);
			}

			var metadata = new SongMetadata(
				artists.Count > 0 ? artists : null,
				tag.Album,
				tag.Title,
				(int?)tag.Track > 0 ? (int?)tag.Track : null,
				(int?)tag.TrackCount > 0 ? (int?)tag.TrackCount : null,
				null, // ArtworkUrl - could extract from tag if needed
				file.Properties.Duration
			);

			OnMediaMetadataAvailable(metadata);
		} catch (Exception ex) {
			_logger.LogError(ex, "Error extracting metadata for {Uri}", uri);
		}
	}

	public static void TestPlayback()
	{
		var regex = StatusLineRegex();

		using var process = new Process() {
			StartInfo = new() {
				FileName = "ffplay",
				Arguments = $"-autoexit -nodisp -vn -hide_banner -loglevel warning -stats  -",
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};

		process.Start();

		// Start tasks to read stdout and stderr asynchronously
		var stdoutTask = Task.Run(() => {
			try {
				string? line;
				while ((line = process.StandardOutput.ReadLine()) != null) {
					Console.WriteLine($"[STDOUT] {line}");
				}
			} catch (Exception ex) {
				Console.WriteLine($"Error reading stdout: {ex.Message}");
			}
		});

		var stderrTask = Task.Run(() => {
			try {
				string? line;
				while ((line = process.StandardError.ReadLine()) != null) {
					if (TryParseStatus(line, regex, out var pos, out var sync, out var aq)) {
						Console.WriteLine($"pos={pos} sync={sync} aq={aq}");
					} else {
						Console.WriteLine($"[STDERR] {line}");
					}
				}
			} catch (Exception ex) {
				Console.WriteLine($"Error reading stderr: {ex.Message}");
			}
		});

		using var fs = System.IO.File.OpenRead(@"\\vmhost.local\media\Audio\Full CDs\Collective Soul\Collective Soul\07 - Gel.mp3");

		// copy the data from fs to the stdin of the process
		fs.CopyTo(process.StandardInput.BaseStream);
		process.StandardInput.Close();

		// Wait for the process and reading tasks to complete
		process.WaitForExit();
		Task.WaitAll(stdoutTask, stderrTask);
	}

	private static bool TryParseStatus(string line, Regex regex, out TimeSpan pos, out double sync, out int aq)
	{
		var m = regex.Match(line);

		if (!m.Success) {
			pos  = TimeSpan.Zero;
			sync = 0;
			aq   = 0;
			return false;
		}

		if (!double.TryParse(m.Groups["pos"].Value, out var posSeconds)) {
			throw new InvalidOperationException($"Unable to parse value [{m.Groups["pos"].Value}] for playback position.");
		}

		if (!double.TryParse(m.Groups["sync"].Value, out sync)) {
			throw new InvalidOperationException($"Unable to parse value [{m.Groups["sync"].Value}] for sync value.");
		}

		if (!int.TryParse(m.Groups["aq"].Value, out aq)) {
			throw new InvalidOperationException($"Unable to parse value [{m.Groups["aq"].Value}] for aq value.");
		}

		pos = posSeconds switch {
			double.NaN => TimeSpan.Zero,
			<0 => TimeSpan.Zero,
			_ => TimeSpan.FromSeconds(posSeconds),
		};

		return true;
	}

	[GeneratedRegex(@"^\s*(?<pos>-?\d+\.\d+|nan)\s+(M-A|):\s+(?<sync>-?\d+\.\d+|nan)\s+fd=\s*(?<fd>\d+)\s+aq=\s*(?<aqg>(?<aq>\d+)KB|nan)\s+vq=\s*(?<vq>\d+KB|nan)\s+sq=\s*(?<sq>\d+[BK]|nan)\s*$", RegexOptions.Compiled)]
	private static partial Regex StatusLineRegex();
}
