using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SongServer.Services;

public class Playlist
{
	private readonly static FrozenSet<string> _extensions = [".m3u", ".m3u8"];

	public static Playlist Empty { get; } = new Playlist([], string.Empty);

	private readonly List<Uri> _entries;

	public Playlist(IEnumerable<string> entries)
	{
		using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		_entries = [];

		foreach (var entry in entries) {
			hasher.AppendData(MemoryMarshal.AsBytes(entry.AsSpan()));
			_entries.Add(ConvertToUri(entry));
		}

		Hash = Convert.ToHexStringLower(hasher.GetCurrentHash());
	}

	private Playlist(List<Uri> entries, string hash)
	{
		_entries = entries;
		Hash     = hash;
	}

	public IReadOnlyList<Uri> Entries => _entries;

	public string Hash { get; }

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

	public void Shuffle(int seed)
	{
		var random = new Random(seed);

		// shuffle the list using fisher-yates
		for (var i = _entries.Count - 1; i > 0; i--) {
			var j = random.Next(0, i + 1);
			(_entries[j], _entries[i]) = (_entries[i], _entries[j]);
		}
	}

	public static async Task<Playlist> Load(string path)
	{
		var visitedFiles = new HashSet<string>();
		var entries      = new List<Uri>();

		using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		await LoadList(path, entries, visitedFiles, hasher);

		return new Playlist(entries, Convert.ToHexStringLower(hasher.GetCurrentHash()));
	}

	private static async Task LoadList(string listPath, List<Uri> entries, HashSet<string> visitedFiles, IncrementalHash hasher)
	{
		if (!visitedFiles.Add(Path.GetFullPath(listPath))) {
			return;
		}

		if (!File.Exists(listPath)) {
			throw new FileNotFoundException($"Playlist file not found: {listPath}");
		}

		var listDir = Path.GetDirectoryName(listPath) ?? string.Empty;
		var lines = (await File.ReadAllLinesAsync(listPath))
			.Select(l => l.Trim())
			.Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
			.Select(l => Path.IsPathRooted(l) ? l : Path.Combine(listDir, l));

		foreach (var line in lines) {
			if (_extensions.Contains(Path.GetExtension(line), StringComparer.OrdinalIgnoreCase)) {
				await LoadList(line, entries, visitedFiles, hasher);
			} else {
				hasher.AppendData(MemoryMarshal.AsBytes(line.AsSpan()));
				entries.Add(ConvertToUri(line));
			}
		}
	}
}
