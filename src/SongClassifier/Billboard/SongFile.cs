using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SongClassifier.Billboard;

[Table("files")]
public class SongFile
{
	public SongFile() {}

	[SetsRequiredMembers]
	public SongFile(string artist, string album, string title, string filename)
	{
		Artist   = artist;
		Album    = album;
		Title    = title;
		Filename = filename;
	}

	[Column("artist")]
	public required string Artist { get; set; }

	[Column("album")]
	public required string Album { get; set; }

	[Column("title")]
	public required string Title { get; set; }

	[Column("filename")]
	public required string Filename { get; set; }

	[Column("track")]
	public int? Track { get; set; }
}
