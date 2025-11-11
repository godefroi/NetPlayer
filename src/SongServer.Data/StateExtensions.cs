using System.Text.Json;

namespace SongServer.Data;

public static class StateExtensions
{
	public static async Task<T?> GetStateAsync<T>(this DbSet<State> set, string key, CancellationToken cancellationToken = default)
	{
		var stateRow = await set.FindAsync([key], cancellationToken);

		if (stateRow == null || string.IsNullOrWhiteSpace(stateRow.Value)) {
			return default;
		}

		return JsonSerializer.Deserialize<T>(stateRow.Value);
	}

	public static T? GetState<T>(this DbSet<State> set, string key)
	{
		var stateRow = set.Find([key]);

		if (stateRow == null || string.IsNullOrWhiteSpace(stateRow.Value)) {
			return default;
		}

		return JsonSerializer.Deserialize<T>(stateRow.Value);
	}

	public static async Task SetState<T>(this DbSet<State> set, string key, T value, CancellationToken cancellationToken = default)
	{
		var stateVal = JsonSerializer.Serialize(value);

		var stateRow = await set.FindAsync([key], cancellationToken);

		if (stateRow == null) {
			set.Add(new State() { Key = key, Value = stateVal });
		} else {
			stateRow.Value = stateVal;
		}
	}
}
