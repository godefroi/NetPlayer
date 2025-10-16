namespace SongServer.Services;

public class StateChangedEventArgs : EventArgs
{
	/// <summary>
	/// Current player state (Playing/Paused/Stopped).
	/// </summary>
	public State? State { get; init; }

	/// <summary>
	/// Current media/song metadata when available.
	/// </summary>
	public SongMetadata? Song { get; init; }

	/// <summary>
	/// Current playback position when available.
	/// </summary>
	public TimeSpan? Position { get; init; }
}
