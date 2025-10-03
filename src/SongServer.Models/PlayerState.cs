using System.Text.Json.Serialization;

namespace SongServer;

public record PlayerState(
	[property: JsonConverter(typeof(JsonStringEnumConverter<State>))]
	State State,

	SongMetadata? Song,

	int QueueLength,

	int ListLength
);
