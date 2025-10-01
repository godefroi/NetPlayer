using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SongServer.Services.Library;

// TODO: probably artist and album should be normalized out... albums have multiple artists

public class Song
{
	public string SourcePath { get { return Source.Path; } }

	public required string RelativePath { get; set; }

	public Uri Url { get { return ConvertToUri(Path.Combine(SourcePath, RelativePath)); } }

	public required string Artist { get; set; }

	public required string Album { get; set; }

	public required string Title { get; set; }

	public int? TrackNumber { get; set; }

	public int? TrackTotal { get; set; }

	public TimeSpan Duration { get; set; }

	public DateTime FileModified { get; set; }

	public long FileSize { get; set; }

	public required string FileHash { get; set; }

	public long SourceId { get; set; }

	public Source Source { get; set; } = null!;

	public static Uri ConvertToUri(string path)
	{
		// If it's already a valid URI, use it
		if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) {
			return uri;
		}

		// Handle Windows UNC paths (\\server\share\path)
		if (path.StartsWith(@"\\")) {
			return new Uri($"file://{path.Replace('\\', '/')}");
		}

		// Handle Unix UNC paths (//server/share/path)
		if (path.StartsWith("//")) {
			return new Uri($"file:{path}");
		}

		// Handle regular file system paths
		try {
			return new Uri(Path.GetFullPath(path));
		} catch {
			// If all else fails, try to create as relative URI
			return new Uri(path, UriKind.RelativeOrAbsolute);
		}
	}
}

internal class SongConfiguration : IEntityTypeConfiguration<Song>
{
	public void Configure(EntityTypeBuilder<Song> builder)
	{
		builder.ToTable("songs");

		builder.HasKey(s => new { s.SourceId, s.RelativePath });

		builder.HasOne(s => s.Source)
			.WithMany()
			.HasForeignKey(s => s.SourceId)
			.HasPrincipalKey(src => src.Id);

		builder.Property(s => s.SourceId)
			.HasColumnName("source_id")
			.IsRequired();

		builder.Property(s => s.RelativePath)
			.HasColumnName("path")
			.IsRequired();

		builder.Property(s => s.FileHash)
			.HasColumnName("hash")
			.IsRequired();

		builder.Property(s => s.Artist)
			.HasColumnName("artist")
			.IsRequired();

		builder.Property(s => s.Album)
			.HasColumnName("album")
			.IsRequired();

		builder.Property(s => s.Title)
			.HasColumnName("title")
			.IsRequired();

		builder.Property(s => s.TrackNumber)
			.HasColumnName("track_number");

		builder.Property(s => s.TrackTotal)
			.HasColumnName("track_total");

		builder.Property(s => s.Duration)
			.HasColumnName("duration")
			.IsRequired();

		builder.Property(s => s.FileModified)
			.HasColumnName("last_modified")
			.IsRequired();

		builder.Property(s => s.FileSize)
			.HasColumnName("file_size")
			.IsRequired();

		builder.Navigation(s => s.Source)
			.AutoInclude();

		// we could index things, but why?
	}
}
