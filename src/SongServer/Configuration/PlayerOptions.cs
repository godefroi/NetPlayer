using System.Text.Json.Serialization;

namespace SongServer.Configuration;

public class PlayerOptions
{
	public const string SectionName = "Player";

	[JsonPropertyName("Library")]
	public required List<string> LibraryFolders { get; set; }

	[JsonPropertyName(nameof(Playlist))]
	public required string Playlist { get; set; }
}
