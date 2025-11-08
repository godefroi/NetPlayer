using System.Text.Json.Serialization;

namespace SongServer;

public record SongReference(
	[property: JsonPropertyName("sourceId")]
	long SourceId,

	[property: JsonPropertyName("relativePath")]
	Uri RelativePath
);
