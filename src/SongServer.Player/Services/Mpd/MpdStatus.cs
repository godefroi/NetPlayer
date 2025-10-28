using System.Diagnostics.CodeAnalysis;

namespace SongServer.Services.Mpd;

public enum PlayState
{
	Play,
	Stop,
	Pause
}

public record MpdStatus(TimeSpan Elapsed, TimeSpan Duration, PlayState State)
{
	public static MpdStatus FromMpdResponse(MpdClient.MpdResponse response)
	{
		if (response.Values == null) {
			throw new ArgumentException("MpdResponse does not contain values");
		}

		if (response.Error != null) {
			throw new InvalidOperationException($"MpdResponse contains an error: {response.Error.Message}");
		}

		// Parse elapsed time (in seconds)
		var elapsed = TryGetValue(response.Values, "elapsed", out var elapsedStr)
			? TimeSpan.FromSeconds(double.Parse(elapsedStr))
			: TimeSpan.Zero;

		// Parse duration (in seconds)
		var duration = TryGetValue(response.Values, "duration", out var durationStr)
			? TimeSpan.FromSeconds(double.Parse(durationStr))
			: TimeSpan.Zero;

		// Parse state
		var state = TryGetValue(response.Values, "state", out var stateStr)
			? stateStr switch {
				"play" => PlayState.Play,
				"stop" => PlayState.Stop,
				"pause" => PlayState.Pause,
				_ => throw new ArgumentException($"Unknown state: {stateStr}")
			}
			: PlayState.Stop;

		return new MpdStatus(elapsed, duration, state);
	}

	private static bool TryGetValue(List<KeyValuePair<string, string>> values, string key, [NotNullWhen(true)]out string? value)
	{
		values = [.. values.Where(p => p.Key == key)];

		if (values.Count > 0) {
			value = values[0].Value;
			return true;
		} else {
			value = null;
			return false;
		}
	}
}
