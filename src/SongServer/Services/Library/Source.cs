using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SongServer.Services.Library;

public class Source
{
	public long Id { get; set; }

	public required string Path { get; set; }
}

internal class SourceConfiguration : IEntityTypeConfiguration<Source>
{
	public void Configure(EntityTypeBuilder<Source> builder)
	{
		builder.ToTable("sources");

		builder.HasKey(s => s.Id);

		builder.Property(s => s.Id)
			.HasColumnName("id")
			.ValueGeneratedOnAdd();

		builder.Property(s => s.Path)
			.HasColumnName("path")
			.IsRequired();
	}
}
