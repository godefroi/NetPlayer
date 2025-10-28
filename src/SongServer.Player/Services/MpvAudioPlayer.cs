#if false
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SongServer.Services;

internal class MpvAudioPlayer : AudioPlayer, IDisposable
{
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _cancellationTokenSource = new();
	private readonly Encoding _encoding = new UTF8Encoding(false);
	private readonly SemaphoreSlim _semaphore = new(1, 1);
	private Process? _mpvProcess;
	private Stream? _ipcStream;
	private Task? _listenerTask;
	private long _commandId = 1;
	private bool _disposed = false;

	public MpvAudioPlayer(ILogger<MpvAudioPlayer> logger) : base(logger)
	{
		_logger = logger;

		try {
			var (fn, socketName) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? ("mpv.exe", $"mpv-{Environment.ProcessId}")
				: ("mpv", Path.Combine(Path.GetTempPath(), $"mpv-{Environment.ProcessId}.sock"));

			var socketArg = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? $"\\\\.\\pipe\\{socketName}"
				: socketName;

			var psi = new ProcessStartInfo {
				FileName = fn,
				Arguments = $"--input-ipc-server={socketArg} --idle=yes --video=no",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			_logger.LogInformation("Starting MPV process with socket at {socketPath}", socketArg);

			_mpvProcess = Process.Start(psi);

			if (_mpvProcess == null) {
				throw new InvalidOperationException("Failed to start MPV process");
			}

			_ = Task.Run(async () => {
				var l = await _mpvProcess.StandardOutput.ReadLineAsync();

				while (l != null) {
					_logger.LogInformation("MPV-out: {line}", l);
					l = await _mpvProcess.StandardOutput.ReadLineAsync();
				}
			});

			_ = Task.Run(async () => {
				var l = await _mpvProcess.StandardError.ReadLineAsync();

				while (l != null) {
					_logger.LogError("MPV-err: {line}", l);
					l = await _mpvProcess.StandardError.ReadLineAsync();
				}
			});

			// Connect to the IPC socket/pipe
			_ipcStream = ConnectToIpc(socketName);

			// Start listening for events
			_listenerTask = ListenForEvents(_cancellationTokenSource.Token);

			_logger.LogInformation("MPV player started successfully");

			OnPlayerStateChanged(State.Stopped);
		} catch (Exception ex) {
			_logger.LogError(ex, "Failed to start MPV player");
			Dispose();
			throw;
		}
	}

	public override async Task<bool> Pause()
	{
		await _semaphore.WaitAsync();
		try {
			SendCommand("set_property", "pause", true);
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
			SendCommand("stop");
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error stopping playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	protected override async Task<bool> Play(Uri uri)
	{
		await _semaphore.WaitAsync();
		try {
			// Stop any currently playing media first
			SendCommand("stop");

			// Load the new file
			string path = UriToFilePath(uri);
			SendCommand("loadfile", path);

			_logger.LogInformation("Loading file: {path}", path);
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
			SendCommand("set_property", "pause", false);
			return true;
		} catch (Exception ex) {
			_logger.LogError(ex, "Error resuming playback");
			return false;
		} finally {
			_semaphore.Release();
		}
	}

	private void SendCommand(params object[] args)
	{
		if (_ipcStream == null) {
			throw new InvalidOperationException("Cannot send commands when IPC is not connected");
		}

		try {
			var command = new {
				command = args,
				request_id = Interlocked.Increment(ref _commandId), // we always send request_id but we don't currently use it
				async = true, // allow mpv to run commands asynchronously
			};

			var json = JsonSerializer.Serialize(command);
			_logger.LogDebug("Sending command: {command}", json);

			var data = _encoding.GetBytes(json + "\n");
			_ipcStream.Write(data, 0, data.Length);
			_ipcStream.Flush();
		} catch (Exception ex) {
			_logger.LogError(ex, "Error sending command: {command}", string.Join(", ", args));
		}
	}

	private async Task ListenForEvents(CancellationToken cancellationToken)
	{
		if (_ipcStream == null) {
			throw new InvalidOperationException("Cannot listen for events when IPC is not connected");
		}

		try {
			using var reader = new StreamReader(_ipcStream, _encoding);
			string? line;

			while ((line = await reader.ReadLineAsync(cancellationToken)) != null) {
				try {
					var response = JsonSerializer.Deserialize<MpvResponse>(line);
					if (response != null) {
						HandleResponse(response);
					}
				} catch (JsonException ex) {
					_logger.LogWarning(ex, "Failed to parse MPV response: {response}", line);
				}
			}
		} catch (OperationCanceledException) {
			_logger.LogInformation("Event listener cancelled");
		} catch (Exception ex) {
			_logger.LogError(ex, "Error in event listener");
		}
	}

	private void HandleResponse(MpvResponse response)
	{
		// Handle events
		if (response.@event != null) {
			_logger.LogDebug("MPV event: {event}", response.@event);

			switch (response.@event) {
				case "start-file":
					_logger.LogInformation("Started playing file");
					OnPlayerStateChanged(State.Playing);
					break;

				case "file-loaded":
					_logger.LogInformation("File loaded");
					// Request metadata
					SendCommand("get_property", "metadata");
					break;

				case "pause":
					if (response.data is JsonElement pauseData && (pauseData.ValueKind == JsonValueKind.True || pauseData.ValueKind == JsonValueKind.False)) {
						bool isPaused = pauseData.GetBoolean();
						OnPlayerStateChanged(isPaused ? State.Paused : State.Playing);
					}
					break;

				case "playback-restart":
					OnPlayerStateChanged(State.Playing);
					break;

				case "seek":
					// Position changed, request current position
					SendCommand("get_property", "time-pos");
					break;

				case "end-file":
					if (response.data is JsonElement endFileData && endFileData.TryGetProperty("reason", out var reason)) {
						_logger.LogInformation("Playback ended: {reason}", reason);
						if (reason.GetString() == "eof") {
							OnPlayerStateChanged(State.Stopped);
							Advance();
						}
					}
					break;
			}
		}

		// Handle property changes
		if (response.@event == "property-change" && response.data is JsonElement data) {
			if (data.TryGetProperty("name", out var nameElement) && 
				data.TryGetProperty("value", out var valueElement)) {
				
				string? propName = nameElement.GetString();
				HandlePropertyChange(propName, valueElement);
			}
		}
	}

	private void HandlePropertyChange(string? propertyName, JsonElement value)
	{
		switch (propertyName) {
			case "time-pos":
				if (value.ValueKind == JsonValueKind.Number && 
					value.TryGetDouble(out double timePos)) {
					OnPositionChanged(TimeSpan.FromSeconds(timePos));
				}
				break;

			case "metadata":
				if (value.ValueKind == JsonValueKind.Object) {
					try {
						var metadata = ParseMetadata(value);
						if (metadata != null) {
							OnMediaMetadataAvailable(metadata);
						}
					} catch (Exception ex) {
						_logger.LogWarning(ex, "Failed to parse metadata");
					}
				}
				break;

			case "pause":
				if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) {
					bool isPaused = value.GetBoolean();
					OnPlayerStateChanged(isPaused ? State.Paused : State.Playing);
				}
				break;
		}
	}

	private SongMetadata? ParseMetadata(JsonElement metadataElement)
	{
		var artists = new List<string>();
		var title = GetStringValue(metadataElement, "Title") ?? GetStringValue(metadataElement, "title");
		var album = GetStringValue(metadataElement, "Album") ?? GetStringValue(metadataElement, "album");
		
		// Try to get artist(s)
		var artist = GetStringValue(metadataElement, "Artist") ?? GetStringValue(metadataElement, "artist");
		if (!string.IsNullOrEmpty(artist)) {
			artists.Add(artist);
		}

		// Try to parse track number
		int? trackNumber = null;
		var trackStr = GetStringValue(metadataElement, "Track") ?? GetStringValue(metadataElement, "track");
		if (!string.IsNullOrEmpty(trackStr) && int.TryParse(trackStr, out int track)) {
			trackNumber = track;
		}

		// Try to get duration from the file
		SendCommand("get_property", "duration");

		return new SongMetadata(
			artists.Count > 0 ? artists : null,
			album,
			title ?? "Unknown Title",
			trackNumber,
			null,
			null,
			TimeSpan.Zero
		);
	}

	private string? GetStringValue(JsonElement element, string key)
	{
		if (element.TryGetProperty(key, out var value)) {
			if (value.ValueKind == JsonValueKind.String) {
				return value.GetString();
			}
		}
		return null;
	}

	private Stream ConnectToIpc(string socketName, int timeoutMs = 5000)
	{
		var sw = Stopwatch.StartNew();

		while (sw.ElapsedMilliseconds < timeoutMs) {
			try {
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
					var pipeClient = new NamedPipeClientStream(".", socketName, PipeDirection.InOut/*, PipeOptions.Asynchronous*/);
					pipeClient.Connect(100);
					_logger.LogInformation("Connected to named pipe at {socketName}", socketName);
					//SendCommand("get_version");
					return pipeClient;
				} else {
					var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
					socket.Connect(new UnixDomainSocketEndPoint(socketName));
					_logger.LogInformation("Connected to Unix domain socket at {socketName}", socketName);
					return new NetworkStream(socket, true);
				}
			} catch {
				Thread.Sleep(50);
			}
		}

		throw new TimeoutException("Timed out connecting to mpv");
	}

	private string UriToFilePath(Uri uri)
	{
		if (uri.IsFile) {
			return Uri.UnescapeDataString(uri.LocalPath);
		}
		return uri.ToString();
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		_cancellationTokenSource?.Cancel();
		
		try {
			if (_mpvProcess != null && !_mpvProcess.HasExited) {
				_mpvProcess.Kill();
				_mpvProcess.WaitForExit(5000);
			}
		} catch (Exception ex) {
			_logger.LogError(ex, "Error stopping MPV process");
		}

		_ipcStream?.Dispose();
		_ipcStream = null;
		_mpvProcess?.Dispose();
		_mpvProcess = null;
		_cancellationTokenSource?.Dispose();
		_semaphore?.Dispose();
		_disposed = true;

		GC.SuppressFinalize(this);
	}

	~MpvAudioPlayer()
	{
		Dispose();
	}

	private record MpvResponse(
		[property: JsonPropertyName("event")] string? @event = null,
		[property: JsonPropertyName("data")] object? data = null,
		[property: JsonPropertyName("error")] string? error = null,
		[property: JsonPropertyName("request_id")] long? request_id = null
	);
}
#endif
