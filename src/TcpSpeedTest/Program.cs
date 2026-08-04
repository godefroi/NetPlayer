using System.Diagnostics;
using System.Net.Sockets;

public class TcpSpeedTest
{
	public static async Task Main(string[] args)
	{
		// Parse command line arguments
		if (args.Length < 2) {
			Console.WriteLine("Usage: TcpSpeedTest <ip_address> <port> [data_size_mb]");
			Console.WriteLine("Example: TcpSpeedTest 127.0.0.1 5000 100");
			return;
		}

		var ipAddress = args[0];
		if (!int.TryParse(args[1], out int port)) {
			Console.WriteLine("Error: Port must be a valid integer.");
			return;
		}

		// Data size in MB (default 100 MB)
		var dataSizeMb = 100L;

		if (args.Length >= 3) {
			if (!long.TryParse(args[2], out dataSizeMb)) {
				Console.WriteLine("Error: Data size must be a valid integer.");
				return;
			}
		}

		var dataSizeBytes = dataSizeMb * 1024 * 1024;

		try {
			await RunSpeedTest(ipAddress, port, dataSizeBytes);
		}
		catch (Exception ex) {
			Console.WriteLine($"Error: {ex.Message}");
		}
	}

	private static async Task RunSpeedTest(string ipAddress, int port, long dataSizeBytes)
	{
		using var client = new TcpClient();

		Console.WriteLine($"Connecting to {ipAddress}:{port}...");

		try {
			await client.ConnectAsync(ipAddress, port);
			Console.WriteLine("Connected successfully!");

			using var stream = client.GetStream();
			
			//await SendRandomData(stream, dataSizeBytes);
			await SendSineWave(stream, dataSizeBytes);
		} catch (SocketException ex) {
			Console.WriteLine($"Connection error: {ex.Message}");
		} catch (IOException ex) {
			Console.WriteLine($"I/O error: {ex.Message}");
		}
	}

	private static async Task SendRandomData(NetworkStream stream, long dataSizeBytes)
	{
		const int bufferSize = 1024 * 1024; // 1 MB buffer
		var buffer = new byte[bufferSize];
		var random = new Random();

		var totalBytesSent = 0L;
		var stopwatch = Stopwatch.StartNew();

		Console.WriteLine($"Sending {dataSizeBytes / (1024 * 1024)} MB of random binary data...");

		try {
			while (totalBytesSent < dataSizeBytes) {
				// Determine how many bytes to send in this iteration
				var bytesToSend = (int)Math.Min(bufferSize, dataSizeBytes - totalBytesSent);

				// Fill buffer with random data
				random.NextBytes(buffer);

				// Send data
				await stream.WriteAsync(buffer.AsMemory(0, bytesToSend));
				totalBytesSent += bytesToSend;

				// Display progress
				var percentComplete = totalBytesSent / (double)dataSizeBytes * 100;
				Console.Write($"\rProgress: {percentComplete:F1}% ({totalBytesSent / (1024 * 1024)} MB / {dataSizeBytes / (1024 * 1024)} MB)");
			}

			// Flush any remaining data
			await stream.FlushAsync();
			stopwatch.Stop();

			Console.WriteLine();
			Console.WriteLine();
			ReportTransferRate(totalBytesSent, stopwatch.Elapsed);
		}
		catch (IOException ex) {
			Console.WriteLine($"\nTransfer interrupted: {ex.Message}");
			stopwatch.Stop();
			if (totalBytesSent > 0) {
				Console.WriteLine($"Partial transfer: {totalBytesSent / (1024 * 1024)} MB sent");
				ReportTransferRate(totalBytesSent, stopwatch.Elapsed);
			}
		}
	}

	private static async Task SendSineWave(NetworkStream stream, long dataSizeBytes)
	{
		const int sampleRate = 48000; // 48 kHz
		const int bytesPerSample = 4; // 32-bit
		const int channels = 2; // Stereo
		const int frequency = 440; // A4 note (pleasing frequency for humans)
		const int bufferSize = 1024 * 1024; // 1 MB buffer

		var totalBytesSent = 0L;
		var stopwatch = Stopwatch.StartNew();
		var sampleIndex = 0L;

		Console.WriteLine($"Sending {dataSizeBytes / (1024 * 1024)} MB of PCM audio data (48 kHz, 32-bit stereo, {frequency} Hz sine wave)...");

		try {
			while (totalBytesSent < dataSizeBytes) {
				// Determine how many bytes to send in this iteration
				var bytesToSend = (int)Math.Min(bufferSize, dataSizeBytes - totalBytesSent);

				// Generate sine wave PCM data
				var buffer = GenerateSineWavePcm(sampleIndex, bytesToSend, sampleRate, frequency, channels, bytesPerSample);

				// Send data
				await stream.WriteAsync(buffer, 0, bytesToSend);
				totalBytesSent += bytesToSend;
				sampleIndex += bytesToSend / (bytesPerSample * channels);

				// Display progress
				var percentComplete = totalBytesSent / (double)dataSizeBytes * 100;
				Console.Write($"\rProgress: {percentComplete:F1}% ({totalBytesSent / (1024 * 1024)} MB / {dataSizeBytes / (1024 * 1024)} MB)");
			}

			// Flush any remaining data
			await stream.FlushAsync();
			stopwatch.Stop();

			Console.WriteLine();
			Console.WriteLine();
			ReportTransferRate(totalBytesSent, stopwatch.Elapsed);
		}
		catch (IOException ex) {
			Console.WriteLine($"\nTransfer interrupted: {ex.Message}");
			stopwatch.Stop();
			if (totalBytesSent > 0) {
				Console.WriteLine($"Partial transfer: {totalBytesSent / (1024 * 1024)} MB sent");
				ReportTransferRate(totalBytesSent, stopwatch.Elapsed);
			}
		}
	}

	private static byte[] GenerateSineWavePcm(long startSampleIndex, int bytesToGenerate, int sampleRate, int frequency, int channels, int bytesPerSample)
	{
		var buffer = new byte[bytesToGenerate];
		var samplesNeeded = bytesToGenerate / (bytesPerSample * channels);
		var amplitude = int.MaxValue * 0.8; // Max value for 32-bit signed integer

		for (var i = 0; i < samplesNeeded; i++) {
			// Calculate the sine wave value for this sample
			var sampleIndex = startSampleIndex + i;
			var phase = (2.0 * Math.PI * frequency * sampleIndex) / sampleRate;
			var sineValue = Math.Sin(phase);

			// Convert to 32-bit PCM value
			var pcmValue = (int)(sineValue * amplitude);

			// Write the same value to both channels (mono expanded to stereo)
			for (int channel = 0; channel < channels; channel++) {
				var byteIndex = (i * channels + channel) * bytesPerSample;
				buffer[byteIndex] = (byte)(pcmValue & 0xFF);
				buffer[byteIndex + 1] = (byte)((pcmValue >> 8) & 0xFF);
				buffer[byteIndex + 2] = (byte)((pcmValue >> 16) & 0xFF);
				buffer[byteIndex + 3] = (byte)((pcmValue >> 24) & 0xFF);
			}
		}

		return buffer;
	}

	private static void ReportTransferRate(long bytesTransferred, TimeSpan elapsedTime)
	{
		if (elapsedTime.TotalSeconds == 0) {
			Console.WriteLine("Transfer completed too quickly to measure.");
			return;
		}

		// Calculate transfer rates
		var bytesPerSecond = bytesTransferred / elapsedTime.TotalSeconds;
		var megabitsPerSecond = (bytesPerSecond * 8) / (1024 * 1024);
		var megabytesPerSecond = bytesPerSecond / (1024 * 1024);

		// Format elapsed time
		var timeFormatted = elapsedTime.TotalSeconds < 60 
			? $"{elapsedTime.TotalSeconds:F2} seconds"
			: $"{elapsedTime.TotalMinutes:F2} minutes";

		// Display results
		Console.WriteLine("=== Transfer Complete ===");
		Console.WriteLine($"Total Data Transferred: {bytesTransferred / (1024 * 1024)} MB");
		Console.WriteLine($"Elapsed Time: {timeFormatted}");
		Console.WriteLine($"Transfer Rate: {megabytesPerSecond:F2} MB/s");
		Console.WriteLine($"Transfer Rate: {megabitsPerSecond:F2} Mbps");
		Console.WriteLine($"Raw: {bytesPerSecond:F0} bytes/s");
	}
}
