using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SongServer.Services.Library;

public class Album
{
	public long Id { get; set; }

	public required string Title { get; set; }

	public ICollection<Artist> Artists { get; set; } = [];

	public ICollection<Song> Songs { get; set; } = [];
}

internal class AlbumConfiguration : IEntityTypeConfiguration<Album>
{
	public void Configure(EntityTypeBuilder<Album> builder)
	{
		builder.ToTable("albums");
		builder.HasKey(a => a.Id);

		builder.Property(a => a.Id)
			.HasColumnName("id")
			.ValueGeneratedOnAdd()
			.IsRequired();

		builder.Property(a => a.Title)
			.HasColumnName("title")
			.IsRequired();

		builder.HasMany(a => a.Artists)
			.WithMany(ar => ar.Albums)
			.UsingEntity<Dictionary<string, object>>(
				"album_artists",
				j => j.HasOne<Artist>().WithMany().HasForeignKey("artists_id"),
				j => j.HasOne<Album>().WithMany().HasForeignKey("albums_id"),
				je => {
					je.ToTable("album_artists");
					je.HasKey("albums_id", "artists_id");
					je.Property<long>("albums_id").HasColumnName("albums_id");
					je.Property<long>("artists_id").HasColumnName("artists_id");
				});

		builder.Navigation(a => a.Artists).AutoInclude();
	}
}
