namespace SongServer;

public record PlayerState(
	State State,

	SongMetadata? Song,

	int QueueLength,

	int ListLength
);
