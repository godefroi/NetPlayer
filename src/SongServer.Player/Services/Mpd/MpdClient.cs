using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SongServer.Services.Mpd;

public partial class MpdClient(ILogger<MpdClient> logger)
{
	private static readonly ReadOnlyMemory<byte> _newline;
	private static readonly Encoding _encoding;

	private readonly ILogger _logger = logger;
	private TcpClient? _tcpClient;
	private NetworkStream? _networkStream;
	private PipeReader? _pipeReader;
	private SemaphoreSlim _semaphore = new(1);

	static MpdClient()
	{
		_encoding = new UTF8Encoding(false);
		_newline  = _encoding.GetBytes("\n").AsMemory();
	}

	public async Task<MpdResponse> Connect(string host = "127.0.0.1", int port = 6600, int timeoutMs = 5000, CancellationToken cancellationToken = default)
	{
		var sw  = Stopwatch.StartNew();

		_tcpClient ??= new TcpClient();

		if (_tcpClient.Connected) {
			_tcpClient.Close();
		}

		while (sw.ElapsedMilliseconds < timeoutMs) {
			try {
				await _tcpClient.ConnectAsync(host, port, cancellationToken);
				_networkStream = _tcpClient.GetStream();
				_pipeReader = PipeReader.Create(_networkStream);

				// Read the greeting from MPD
				//var greeting = _reader.ReadLine();
				//_logger.LogInformation("Connected to MPD: {greeting}", greeting);

				// if (!greeting?.StartsWith("OK MPD") ?? true) {
				// 	throw new InvalidOperationException("Invalid MPD greeting");
				// }
				var ret = await ReadResponse();

				if (ret.Error != null) {
					_pipeReader.Complete();
					_pipeReader = null;
					_networkStream.Close();
					_networkStream?.Dispose();
					_networkStream = null;
					_tcpClient.Close();
				}

				return ret;
			} catch (SocketException ex) when (sw.ElapsedMilliseconds < timeoutMs) {
				_logger.LogDebug(ex, "Failed to connect to MPD, retrying...");
				Thread.Sleep(100);
			}
		}

		throw new TimeoutException($"Timed out connecting to MPD at {host}:{port}");
	}

	public async Task<MpdResponse> SendCommand(string command, CancellationToken cancellationToken = default)
	{
		if (_networkStream == null || _pipeReader == null) {
			throw new InvalidOperationException("Cannot send commands when not connected to MPD");
		}

		_logger.LogDebug("Sending MPD command: {command}", command.Trim());

		var maxBytes = _encoding.GetMaxByteCount(command.Length + _newline.Length);
		var cbuffer  = ArrayPool<byte>.Shared.Rent(maxBytes);

		await _semaphore.WaitAsync(cancellationToken);

		try {
			var byteCount = _encoding.GetBytes(command.AsSpan(), cbuffer);
			_newline.CopyTo(cbuffer.AsMemory()[byteCount..]);
			_logger.LogTrace("Sending command string of {len} bytes: {bytes}", byteCount + _newline.Length, Convert.ToHexStringLower(cbuffer, 0, byteCount + _newline.Length));
			await _networkStream.WriteAsync(cbuffer.AsMemory()[..(byteCount + _newline.Length)], cancellationToken);
			await _networkStream.FlushAsync(cancellationToken);

			return await ReadResponse();
		} finally {
			ArrayPool<byte>.Shared.Return(cbuffer);
			_semaphore.Release();			
		}
	}

	public async Task<MpdStatus> GetStatus(CancellationToken cancellationToken = default)
	{
		var response = await SendCommand("status", cancellationToken);
		return MpdStatus.FromMpdResponse(response);
	}

	public async Task<MountList> GetMounts(CancellationToken cancellationToken = default)
	{
		var response = await SendCommand("listmounts", cancellationToken);
		return MountList.FromMpdResponse(response);
	}

	public async Task<List<string>> GetChangedSubsystems(CancellationToken cancellationToken = default)
	{
		var response = await SendCommand("idle", cancellationToken);

		return [.. (response.Values ?? [])
			.Where(v => v.Key == "changed")
			.Select(v => v.Value)];
	}

	private async Task<MpdResponse> ReadResponse()
	{
		// TODO: we should have a cancellationtoken here.. but how do we deal with the leftover response when we get cancelled before a response comes in?
		if (_pipeReader == null) {
			throw new InvalidOperationException("Cannot read responses when not connected.");
		}

		var values = default(List<KeyValuePair<string, string>>?);
		//var binaryData = default(byte[]?);

		try {
			// read data from the pipe until we get a complete response
			while (true) {
				var result = await _pipeReader.ReadAsync();
				ReadOnlySequence<byte> buffer = result.Buffer;

				// try to parse a complete line from the buffer
				while (TryReadLine(ref buffer, out string? line)) {
					// We got a complete line
					_logger.LogTrace("MPD response line: {line}", line);

					// Tell the pipe reader we consumed this data
					_pipeReader.AdvanceTo(buffer.Start);

					// if the response is complete (we got OK or ACK), exit
					if (line.StartsWith("OK")) {
						_logger.LogDebug("MPD response complete (OK): {line}", line);
						if (line == "OK") {
							// normal OK without the version
							return new MpdResponse(values);
						} else {
							// parse the initial connection banner
							return new MpdResponse(values, MpdVersion: line.Split(' ')[2]);
						}
					} else if (line.StartsWith("ACK")) {
						_logger.LogDebug("MPD response complete (ACK): {line}", line);
						var match = ErrorRegex().Match(line);
						return new MpdResponse(values, Error: match.Success switch {
							true => new(int.Parse(match.Groups["errorCode"].Value), int.Parse(match.Groups["listNum"].Value), match.Groups["command"].Value, match.Groups["message"].Value),
							false => new(-1, -1, "UNKNOWN", "-ACK parse failure-"),
						});
					} else if (line.StartsWith("binary: ")) {
						throw new NotImplementedException("Binary responses are not yet implemented.");
					} else {
						// normal key-value pair
						values ??= [];
						var parts = line.Split(": ", 2);
						values.Add(KeyValuePair.Create(parts[0], parts[1]));
					}
				}
				
				if (result.IsCompleted) {
					// Connection closed without getting a complete response
					throw new InvalidOperationException("Connection closed before receiving complete response");
				} else {
					// More data needed, tell the pipe reader how much we examined
					_pipeReader.AdvanceTo(buffer.Start, buffer.End);
				}
			}
		} catch (Exception ex) {
			_logger.LogError(ex, "Error reading MPD response");
			throw;
		}
	}

	private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)]out string? line)
	{
		// look for a newline character in the buffer
		var position = buffer.PositionOf((byte)'\n');

		if (position == null) {
			line = null;
			return false;
		}

		// we found a newline, extract the line
		line = _encoding.GetString(buffer.Slice(0, position.Value));

		// update the buffer
		buffer = buffer.Slice(buffer.GetPosition(1, position.Value));

		return true;
	}

	[GeneratedRegex(@"ACK \[(?<errorCode>\d+)@(?<listNum>\d+)\] \{(?<command>[^}]+)\} (?<message>.+)")]
	internal static partial Regex ErrorRegex();

	public record MpdError(int ErrorCode, int ListNum, string Command, string Message);

	public record MpdResponse(List<KeyValuePair<string, string>>? Values, byte[]? BinaryData = null, MpdError? Error = null, string? MpdVersion = null);
}
