using System.ComponentModel.DataAnnotations.Schema;

namespace SongClassifier.Billboard;

public abstract class Song
{
	[Column("date")]
	public required string Date { get; set; }

	[Column("title")]
	public required string Title { get; set; }

	[Column("artist")]
	public required string Artist { get; set; }

	[Column("rank")]
	public int Rank { get; set; }

	[Column("last_week")]
	public int? LastWeek { get; set; }

	[Column("peak_pos")]
	public int PeakPos { get; set; }

	[Column("weeks_on_chart")]
	public int? WeeksOnChart { get; set; }

	[Column("image_url")]
	public string? ImageUrl { get; set; }
}

public class RadioSong : Song { }

public class Hot100Song : Song { }
