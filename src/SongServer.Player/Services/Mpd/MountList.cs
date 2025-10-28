namespace SongServer.Services.Mpd;

public record MountList(Dictionary<string, string> Mounts)
{
	public static MountList FromMpdResponse(MpdClient.MpdResponse response)
	{
		if (response.Values == null) {
			throw new ArgumentException("MpdResponse does not contain values");
		}

		if (response.Error != null) {
			throw new InvalidOperationException($"MpdResponse contains an error: {response.Error.Message}");
		}

		var mounts = new Dictionary<string, string>();

		string? mount = default;
		string? storage = default;

		// Iterate through the response values in order to maintain mount/storage pairs
		foreach (var kvp in response.Values) {
			if (kvp.Key == "mount") {
				if (mount != null || storage != null) {
					throw new InvalidOperationException("Starting a new mount without finishing the previous mount");
				}
				mount = kvp.Value;
			} else if (kvp.Key == "storage") {
				if (mount != null) {
					mounts.Add(mount, kvp.Value);
					mount = null;
				} else {
					throw new InvalidOperationException("Received storage value without previous mount");
				}
			} else {
				throw new InvalidOperationException("Unrecognized response key/value pair");
			}
		}

		if (mount != null) {
			mounts.Add(mount, string.Empty);
		}

		return new MountList(mounts);
	}
}
