namespace SongServer.Data;

public class State
{
	public required string Key { get; set; }

	public required string Value { get; set; }
}

internal class StateConfiguration : IEntityTypeConfiguration<State>
{
	public void Configure(EntityTypeBuilder<State> builder)
	{
		builder.ToTable("state");

		builder.HasKey(s => s.Key);

		builder.Property(s => s.Key)
			.HasColumnName("key");

		builder.Property(s => s.Value)
			.HasColumnName("value");
	}
}
