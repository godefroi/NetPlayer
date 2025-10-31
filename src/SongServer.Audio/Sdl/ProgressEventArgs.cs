namespace SongServer.Audio.Sdl;

public class ProgressEventArgs : EventArgs
{
	public required TimeSpan Position { get; init; }
}
