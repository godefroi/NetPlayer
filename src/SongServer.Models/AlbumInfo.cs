using System.Diagnostics;

namespace SongServer;

public record AlbumInfo(List<string> Artists, string Album)
{
	public string ArtistOrVarious() => Artists.Count switch {
			0 => "Unknown Artist",
			1 => Artists[0],
			>1 => "Various Artists",
			_ => throw new UnreachableException(),
		};

	public string ArtistOrArtists() => Artists.Count switch {
			0 => "Unknown Artist",
			1 => Artists[0],
			>1 => string.Join(", ", Artists),
			_ => throw new UnreachableException(),
		};
}
