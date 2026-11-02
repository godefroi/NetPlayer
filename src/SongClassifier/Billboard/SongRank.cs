using System.ComponentModel.DataAnnotations.Schema;

namespace SongClassifier.Billboard;

public class SongRank
{
	[Column("artist")]
	public required string Artist { get; set; }

	[Column("title")]
	public required string Title { get; set; }

	[Column("rank")]
	public int Rank { get; set; }
}
