namespace SongServer.Services.Library;

public class LibrarySong
{
	public required string FilePath { get; set; }
	public required string FileHash { get; set; }
	public required string Artist { get; set; }
	public required string Album { get; set; }
	public required string Title { get; set; }
	public int TrackNumber { get; set; }
	public int TrackTotal { get; set; }
	public TimeSpan Duration { get; set; }
	public DateTime LastModified { get; set; }
	public long FileSize { get; set; }
}
