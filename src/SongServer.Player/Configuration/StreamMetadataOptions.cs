namespace SongServer.Configuration;

public class StreamMetadataOptions
{
	public const string SectionName = "StreamMetadata";

	public required Uri RefererUri { get; init; }

	public required Uri MetadataUri { get; init; }
}
