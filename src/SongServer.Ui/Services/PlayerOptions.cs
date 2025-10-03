namespace SongServer.Services;

public class PlayerOptions
{
	public const string SectionName = "Player";

	public required Uri ServerUri { get; set; } = new Uri("http://songs.local/");
}
