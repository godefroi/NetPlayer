using System.ComponentModel.DataAnnotations.Schema;

namespace SongClassifier.Billboard;

public class RankedFile
{
	[Column("filename")]
	public required string Filename { get; set; }

	[Column("artist")]
	public required string Artist { get; set; }

	[Column("album")]
	public required string Album { get; set; }

	[Column("title")]
	public required string Title { get; set; }

	[Column("track")]
	public int? Track { get; set; }

	[Column("in_playlist")]
	public required bool InPlaylist { get; set; }

	[Column("rank")]
	public int? Rank { get; set; }
}
