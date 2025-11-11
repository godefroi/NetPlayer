namespace SongServer;

public record PlaylistView(List<SongMetadata> Previous, SongMetadata Current, List<SongMetadata> Upcoming);
