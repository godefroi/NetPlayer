namespace SongServer.Data;

public class PlaylistFile
{
	public required long SourceId { get; set; }

	public required string RelativePath { get; set; }

	public Source? Source { get; set; }

	public Song? Song { get; set; }
}

public class PlaylistFileConfiguration : IEntityTypeConfiguration<PlaylistFile>
{
	public void Configure(EntityTypeBuilder<PlaylistFile> builder)
	{
		builder.ToTable("playlist");

		builder.HasKey(p => new { p.SourceId, p.RelativePath });

		builder.Property(p => p.SourceId)
			.HasColumnName("source_id")
			.IsRequired();

		builder.Property(p => p.RelativePath)
			.HasColumnName("relative_path")
			.IsRequired();

		builder.HasOne(p => p.Source)
			.WithMany()
			.HasForeignKey(p => new { p.SourceId })
			.OnDelete(DeleteBehavior.Cascade);

		builder.HasOne(p => p.Song)
			.WithMany()
			.HasForeignKey(p => new { p.SourceId, p.RelativePath })
			.OnDelete(DeleteBehavior.Cascade);
	}
}
