using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SongServer.Services;

public class Playlist
{
	private readonly static FrozenSet<string> _extensions = [".m3u", ".m3u8"];
	private readonly static IComparer<SongReference> _comparer = Comparer<SongReference>.Create(CompareSongs);

	public static Playlist Empty { get; } = new Playlist([]);

	private readonly List<SongReference> _entries;
	private readonly List<int> _indices;
	private int _current = -1;
	private int _seed = -1;

	public Playlist(IEnumerable<SongReference> entries)
	{
		_entries = [..entries];
		_indices = [..Enumerable.Range(0, _entries.Count)];
		Hash     = HashList(_entries);
	}

	public string Hash { get; }

	public int Count => _entries.Count;

	public int ShuffleSeed => _seed;

	public int CurrentIndex => _current;

	public void Add(SongReference songReference)
	{
		var idx = _entries.BinarySearch(songReference, _comparer);

		if (idx > 0) {
			// we already have this one
			return;
		}

		// insert the new item
		_entries.Insert(~idx, songReference);

		if (_seed > -1) {
			// and now we need to recreate the indices and shuffle them
			Shuffle(_seed);
		} else {
			// just recreate the indices without shuffling
			_indices.Clear();
			_indices.EnsureCapacity(_entries.Count);
			_indices.AddRange(Enumerable.Range(0, _entries.Count));
		}
	}

	public void Remove(SongReference songReference)
	{
		var idx = _entries.BinarySearch(songReference, _comparer);

		if (idx < 0) {
			// we don't have this one
			return;
		}

		// remove it
		_entries.RemoveAt(idx);

		if (_seed > -1) {
			// and now we need to recreate the indices and shuffle them
			Shuffle(_seed);
		} else {
			// just recreate the indices without shuffling
			_indices.Clear();
			_indices.EnsureCapacity(_entries.Count);
			_indices.AddRange(Enumerable.Range(0, _entries.Count));
		}
	}

	public void Shuffle(int seed, int? currentIndex = null)
	{
		_seed = seed;

		var random = new Random(seed);

		// recreate the indices (so shuffling with the same seed always gives the same results)
		_indices.Clear();
		_indices.AddRange(Enumerable.Range(0, _entries.Count));

		// shuffle the list using fisher-yates
		for (var i = _indices.Count - 1; i > 0; i--) {
			var j = random.Next(0, i + 1);
			(_indices[j], _indices[i]) = (_indices[i], _indices[j]);
		}

		if (currentIndex != null) {
			_current = currentIndex.Value;
		}
	}

	/// <summary>
	/// Move to the previous item in the playlist and return it
	/// </summary>
	/// <returns>The previous <see cref="SongReference"/> in the playlist</returns>
	public SongReference Previous()
	{
		_current--;
		
		if (_current < 0) {
			_current = _indices.Count - 1;
		}
		
		return _entries[_indices[_current]];
	}

	/// <summary>
	/// Move to the next item in the playlist and return it
	/// </summary>
	/// <returns>The next <see cref="SongReference"/> in the playlist</returns>
	public SongReference Next()
	{
		_current++;
		
		if (_current >= _indices.Count) {
			_current = 0;
		}
		
		return _entries[_indices[_current]];
	}

	public (List<SongReference> Previous, SongReference Current, List<SongReference> Upcoming) GetCurrentView(int context = 10)
	{
		var prev = new List<SongReference>(context);
		var next = new List<SongReference>(context);

		if (Count == 0) {
			return new(prev, new SongReference(-1, new Uri("about:empty")), next);
		}

		for (var i = 1; i <= context; i++) {
			var prevIdx = (_current - i + _indices.Count) % _indices.Count;
			prev.Insert(0, _entries[_indices[prevIdx]]);

			var nextIdx = (_current + i) % _indices.Count;
			next.Add(_entries[_indices[nextIdx]]);
		}

		return new(prev, _entries[_indices[_current]], next);
	}

	private static string HashList(IEnumerable<SongReference> songs)
	{
		using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		Span<char> stackBuffer = stackalloc char[512];

		foreach (var entry in songs) {
			var sid = entry.SourceId;

			// hash the source ID
			hasher.AppendData(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref sid, 1)));

			// hash the relative path
			if (entry.RelativePath.TryFormat(stackBuffer, out var sbLen)) {
				hasher.AppendData(MemoryMarshal.AsBytes(stackBuffer[..sbLen]));
			} else {
				var heapBuffer = ArrayPool<char>.Shared.Rent(2048);
				var bufferSpan = heapBuffer.AsSpan();

				try {
					if (entry.RelativePath.TryFormat(bufferSpan, out var hbLen)) {
						hasher.AppendData(MemoryMarshal.AsBytes(bufferSpan[..hbLen]));
					} else {
						throw new Exception("Unable to format URI into stack- or heap- buffer.");
					}
				} finally {
					ArrayPool<char>.Shared.Return(heapBuffer);
				}
			}
		}

		return Convert.ToHexStringLower(hasher.GetCurrentHash());
	}

	private static int CompareSongs(SongReference x, SongReference y)
	{
		var ret = x.SourceId.CompareTo(y.SourceId);

		if (ret != 0) {
			return ret;
		}

		return x.RelativePath.OriginalString.CompareTo(y.RelativePath.OriginalString);
	}

	private static async Task LoadList(string listPath, int sourceId, List<SongReference> entries, HashSet<string> visitedFiles, IncrementalHash hasher)
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
				await LoadList(line, sourceId, entries, visitedFiles, hasher);
			} else {
				hasher.AppendData(MemoryMarshal.AsBytes(line.AsSpan()));
				entries.Add(new SongReference(sourceId, new Uri(line, UriKind.Relative)));
			}
		}
	}
}
