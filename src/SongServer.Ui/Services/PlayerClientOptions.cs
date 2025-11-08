namespace SongServer.Services;

public class PlayerClientOptions
{
	public const string SectionName = "Player";

	public required Uri ServerUri { get; set; } = new Uri("http://music.local/");
}
