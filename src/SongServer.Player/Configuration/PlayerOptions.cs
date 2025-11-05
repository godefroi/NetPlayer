namespace SongServer.Configuration;

public class PlayerOptions
{
	public const string SectionName = "Player";

	public string Device { get; set; } = string.Empty;

	public double Gain { get; set; } = 1.0d;
}
