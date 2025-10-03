namespace SongServer;

public record SongMetadata(
	string? Artist,

	string? Album,

	string? Title,

	int TrackNumber,

	int TrackTotal,

	string? ArtworkUrl,

	TimeSpan Duration
);
