using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SongServer.Services.Library;

public class JsonLibrary
{
	private static readonly JsonSerializerOptions _jsonOptions = new() {
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	public async Task Store(IEnumerable<JsonSong> songs, CancellationToken cancellationToken = default)
	{
		using var jsonFile = File.Create("songs.json");

		await JsonSerializer.SerializeAsync(jsonFile, songs, _jsonOptions, cancellationToken);
	}

	public async IAsyncEnumerable<JsonSong> Load([EnumeratorCancellation]CancellationToken cancellationToken = default)
	{
		if (!File.Exists("songs.json")) {
			yield break;
		}

		using var jsonFile = File.OpenRead("songs.json");

		var songs = JsonSerializer.DeserializeAsyncEnumerable<JsonSong>(jsonFile, _jsonOptions, cancellationToken);

		await foreach (var song in songs) {
			if (song != null) {
				yield return song;
			}
		}
	}

	public class JsonSong
	{
		public required string SourcePath { get; set; }

		public required string RelativePath { get; set; }

		[JsonIgnore]
		public Uri Url { get { return ConvertToUri(Path.Combine(SourcePath, RelativePath)); } }

		public string? Artist { get; set; }

		public string? Album { get; set; }

		public string? Title { get; set; }

		public int? TrackNumber { get; set; }

		public int? TrackTotal { get; set; }

		public TimeSpan? Duration { get; set; }

		public required DateTime FileModified { get; set; }

		public required string FileHash { get; set; }

		public required long FileSize { get; set; }

		private static Uri ConvertToUri(string path)
		{
			// If it's already a valid URI, use it
			if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) {
				return uri;
			}

			// Handle Windows UNC paths (\\server\share\path)
			if (path.StartsWith(@"\\")) {
				return new Uri($"file://{path.Replace('\\', '/')}");
			}

			// Handle Unix UNC paths (//server/share/path)
			if (path.StartsWith("//")) {
				return new Uri($"file:{path}");
			}

			// Handle regular file system paths
			try {
				return new Uri(Path.GetFullPath(path));
			} catch {
				// If all else fails, try to create as relative URI
				return new Uri(path, UriKind.RelativeOrAbsolute);
			}
		}
	}
}
