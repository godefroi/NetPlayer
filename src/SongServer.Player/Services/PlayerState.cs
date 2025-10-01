using System.Text.Json.Serialization;

namespace SongServer.Services;

internal record SongMetadata(
	string? Artist,

	string? Album,

	string? Title,

	int TrackNumber,

	int TrackTotal,

	string? ArtworkUrl,

	TimeSpan Duration,

	TimeSpan Position
);

internal record PlayerState(
	[property: JsonConverter(typeof(JsonStringEnumConverter<AudioPlayer.State>))]
	AudioPlayer.State State,

	SongMetadata? Song,

	int QueueLength,

	int ListLength
);
