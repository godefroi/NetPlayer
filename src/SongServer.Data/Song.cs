namespace SongServer.Data;

public class Song
{
	public Uri SourcePath { get { return Source.Path; } }

	public required Uri RelativePath { get; set; }

	public Uri Url { get { return new Uri(Source.Path, RelativePath); } }

	public required string Title { get; set; }

	public int? TrackNumber { get; set; }

	public int? TrackCount { get; set; }

	public int? DiscNumber { get; set; }

	public int? DiscCount { get; set; }

	public TimeSpan Duration { get; set; }

	public DateTime FileModified { get; set; }

	public long FileSize { get; set; }

	public required string FileHash { get; set; }

	public long SourceId { get; set; }

	public Source Source { get; set; } = null!;

	public string? Album { get; set; }

	public string? AlbumSort { get; set; }

	public List<string>? AlbumArtists { get; set; }

	public List<string>? AlbumArtistsSort { get; set; }

	public List<string>? Performers { get; set; }

	public List<string>? PerformersSort { get; set; }
}

internal class SongConfiguration : IEntityTypeConfiguration<Song>
{
	public void Configure(EntityTypeBuilder<Song> builder)
	{
		builder.ToTable("songs");

		builder.HasKey(s => new { s.SourceId, s.RelativePath });

		builder
			.Ignore(s => s.Url);

		builder.HasOne(s => s.Source)
			.WithMany()
			.HasForeignKey(s => s.SourceId)
			.HasPrincipalKey(src => src.Id);

		builder.Property(s => s.SourceId)
			.HasColumnName("source_id")
			.IsRequired();

		builder.Property(s => s.RelativePath)
			.HasColumnName("path")
			.IsRequired()
			.HasConversion(
				v => v.ToString(),
				v => new Uri(v, UriKind.Relative));

		builder.Property(s => s.FileHash)
			.HasColumnName("hash")
			.IsRequired();

		builder.Property(s => s.Title)
			.HasColumnName("title")
			.IsRequired();

		builder.Property(s => s.TrackNumber)
			.HasColumnName("track_number");

		builder.Property(s => s.TrackCount)
			.HasColumnName("track_total");

		builder.Property(s => s.DiscNumber)
			.HasColumnName("disc_number");

		builder.Property(s => s.DiscCount)
			.HasColumnName("disc_total");

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

		builder.Property(s => s.Album)
			.HasColumnName("album");

		builder.Property(s => s.AlbumSort)
			.HasColumnName("album_sort");

		builder.Property(s => s.AlbumArtists)
			.HasColumnName("album_artists");

		builder.Property(s => s.AlbumArtistsSort)
			.HasColumnName("album_artists_sort");

		builder.Property(s => s.Performers)
			.HasColumnName("performers");

		builder.Property(s => s.PerformersSort)
			.HasColumnName("performers_sort");

		// we could index things, but why?
	}
}
