using Microsoft.Extensions.Logging;

namespace SongServer.Audio.FFmpeg;

public class AudioConverterFactory(ILoggerFactory loggerFactory)
{
	private readonly ILoggerFactory _loggerFactory = loggerFactory;

	public AudioConverter CreateConverter(Stream inputStream) =>
		new(inputStream, _loggerFactory.CreateLogger<AudioConverter>());
}
