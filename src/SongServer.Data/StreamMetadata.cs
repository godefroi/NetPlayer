namespace SongServer.Data;

public class StreamMetadata
{
	public required string Metadata { get; init; }

	public string? Artist { get; private set; }

	public string? Album { get; private set; }

	public string? Title { get; private set; }

	public long StartTime { get; private set; }

	public long? EndTime { get; private set; }
}

public class StreamMetadataConfiguration : IEntityTypeConfiguration<StreamMetadata>
{
	public void Configure(EntityTypeBuilder<StreamMetadata> builder)
	{
		var colOrder = 0;

		builder.ToTable("stream_metadata");

		builder.Property<long>("rowid")
			.HasColumnName("rowid")
			.HasColumnOrder(colOrder++)
			.ValueGeneratedOnAdd();

		builder.HasKey("rowid");

		builder.Property(e => e.Metadata)
			.HasColumnName("metadata")
			.HasColumnOrder(colOrder++)
			.IsRequired();

		builder.Property(e => e.Artist)
			.HasColumnName("artist")
			.HasColumnOrder(colOrder++)
			.HasComputedColumnSql("json_extract(metadata, '$.artist')", stored: false);

		builder.Property(e => e.Album)
			.HasColumnName("album")
			.HasColumnOrder(colOrder++)
			.HasComputedColumnSql("json_extract(metadata, '$.album')", stored: false);

		builder.Property(e => e.Title)
			.HasColumnName("title")
			.HasColumnOrder(colOrder++)
			.HasComputedColumnSql("json_extract(metadata, '$.title')", stored: false);

		builder.Property(e => e.StartTime)
			.HasColumnName("start_time")
			.HasColumnOrder(colOrder++)
			.HasComputedColumnSql("json_extract(metadata, '$.startTime')", stored: false);

		builder.Property(e => e.EndTime)
			.HasColumnName("end_time")
			.HasColumnOrder(colOrder++)
			.HasComputedColumnSql("json_extract(metadata, '$.endTime')", stored: false);

		builder.HasIndex(e => e.StartTime)
			.IsUnique();

		builder.HasIndex(e => new { e.Artist, e.Album, e.Title });
	}
}
