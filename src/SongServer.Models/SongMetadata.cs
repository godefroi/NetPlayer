using System.Diagnostics;

namespace SongServer;

public record SongMetadata(
	List<string>? Artists,

	string? Album,

	string? Title,

	int? TrackNumber,

	int? TrackTotal,

	string? ArtworkUrl,

	TimeSpan Duration
)
{
	public string ArtistOrVarious() => Artists == null ? "Unknown Artist" : Artists.Count switch {
			0 => "Unknown Artist",
			1 => Artists[0],
			>1 => "Various Artists",
			_ => throw new UnreachableException(),
		};

	public string ArtistOrArtists() => Artists == null ? "Unknown Artist" : Artists.Count switch {
			0 => "Unknown Artist",
			1 => Artists[0],
			>1 => string.Join(", ", Artists),
			_ => throw new UnreachableException(),
		};
	
}
