namespace SongServer;

public record PlayerState(
	State State,

	SongMetadata? Song,

	TimeSpan? Position,

	int QueueLength,

	int ListLength
);
