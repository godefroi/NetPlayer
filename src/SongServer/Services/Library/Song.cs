using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SongServer.Services.Library;

// TODO: probably artist and album should be normalized out... albums have multiple artists

public class Song
{
	public required long SourceId { get; set; }

	public required string Path { get; set; }

	public required string Hash { get; set; }

	public required string Artist { get; set; }

	public required string Album { get; set; }

	public required string Title { get; set; }

	public int? TrackNumber { get; set; }

	public int? TrackTotal { get; set; }

	public TimeSpan Duration { get; set; }

	public DateTime LastModified { get; set; }

	public long FileSize { get; set; }

	public Source? Source { get; set; }
}

internal class SongConfiguration : IEntityTypeConfiguration<Song>
{
	public void Configure(EntityTypeBuilder<Song> builder)
	{
		builder.ToTable("songs");

		builder.HasKey(s => new { s.SourceId, s.Path });

		builder.HasOne(s => s.Source)
			.WithMany()
			.HasForeignKey(s => s.SourceId)
			.HasPrincipalKey(src => src.Id);

		builder.Property(s => s.SourceId)
			.HasColumnName("source_id")
			.IsRequired();

		builder.Property(s => s.Path)
			.HasColumnName("path")
			.IsRequired();

		builder.Property(s => s.Hash)
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

		builder.Property(s => s.LastModified)
			.HasColumnName("last_modified")
			.IsRequired();

		builder.Property(s => s.FileSize)
			.HasColumnName("file_size")
			.IsRequired();

		// we could index things, but why?
	}
}
