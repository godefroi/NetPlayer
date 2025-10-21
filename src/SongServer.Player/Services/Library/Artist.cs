using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SongServer.Services.Library;

public class Artist
{
	public long Id { get; set; }

	public required string Name { get; set; }

	public ICollection<Album> Albums { get; set; } = [];
}

internal class ArtistConfiguration : IEntityTypeConfiguration<Artist>
{
	public void Configure(EntityTypeBuilder<Artist> builder)
	{
		builder.ToTable("artists");
		builder.HasKey(a => a.Id);

		builder.Property(a => a.Id)
			.HasColumnName("id")
			.ValueGeneratedOnAdd()
			.IsRequired();

		builder.Property(a => a.Name)
			.HasColumnName("name")
			.IsRequired();
	}
}
