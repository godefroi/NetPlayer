using System.Text.Json.Serialization;

namespace SongServer;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum State
{
	Stopped,
	Paused,
	Playing,
}
