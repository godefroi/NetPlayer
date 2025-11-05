
namespace SongServer.Services;

internal class FfmpegAudioPlayer(ILogger<FfmpegAudioPlayer> logger) : AudioPlayer(logger)
{
	private readonly ILogger _logger = logger;

	public override Task<bool> Pause()
	{
		throw new NotImplementedException();
	}

	public override Task<bool> Stop()
	{
		throw new NotImplementedException();
	}

	protected override Task<bool> Play(SongReference song)
	{
		throw new NotImplementedException();
	}

	protected override Task<bool> Resume()
	{
		throw new NotImplementedException();
	}
}
