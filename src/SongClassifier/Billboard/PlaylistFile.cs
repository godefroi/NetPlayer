using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SongClassifier.Billboard;

[Table("playlist")]
public class PlaylistFile
{
	[SetsRequiredMembers]
	public PlaylistFile(string filename)
	{
		Filename = filename;
	}

	[Column("filename")]
	public required string Filename { get; set; }
}
