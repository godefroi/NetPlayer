using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SongServer.Services.Library;

public class LibraryService(IDbContextFactory<LibraryContext> contextFactory, ILogger<LibraryService>? logger = null) : IDisposable
{
	private readonly static FrozenSet<string> _extensions = [".mp3", ".opus", ".flac"];

	private readonly IDbContextFactory<LibraryContext> _contextFactory = contextFactory;
	private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
	private readonly ILogger<LibraryService>? _logger = logger;

	public void Dispose()
	{
		_scanSemaphore.Dispose();
		GC.SuppressFinalize(this);
	}

	public async Task InitializeAsync(IEnumerable<string> libraryPaths)
	{
		libraryPaths = [.. libraryPaths.Select(Path.GetFullPath)];

		await _scanSemaphore.WaitAsync();

		try {
			using var context = await _contextFactory.CreateDbContextAsync();
			
			// ensure the database is created
			await context.Database.EnsureCreatedAsync();

			// add sources to database if they don't exist
			foreach (var path in libraryPaths) {
				var existingSource = await context.Sources.SingleOrDefaultAsync(s => s.Path == path);

				if (existingSource == null) {
					context.Set<Source>().Add(new Source { Path = path });
				}
			}

			// save the sources
			await context.SaveChangesAsync();
		} finally {
			_scanSemaphore.Release();
		}

		// now refresh the library
		await RefreshAsync();
	}

	public async Task RefreshAsync()
	{
		var updated = 0;
		var removed = 0;
		var seen    = new ConcurrentBag<Uri>();

		await _scanSemaphore.WaitAsync();

		try {
			using var context = await _contextFactory.CreateDbContextAsync();

			// get the list of library paths
			var libraryPaths = await context.Sources.Select(s => s.Path).ToListAsync();

			// scan the source paths
			var (processed, errors) = (await Task.WhenAll(libraryPaths.Select(p => ScanDirectoryAsync(p, seen))))
				.Aggregate((a, b) => (a.Processed + b.Processed, a.Errors + b.Errors));

			var foundSet = seen.ToHashSet();

			// now, go through all the files and remove ones we didn't see
			foreach (var f in context.Songs) {
				if (!foundSet.Contains(f.Url)) {
					context.Songs.Remove(f);
				}
			}

			// save the delete changes
			removed = await context.SaveChangesAsync();
		} finally {
			_scanSemaphore.Release();
		}

		_logger?.LogInformation("Refresh complete; {processed} processed, {updated} added/updated, {removed} removed.", seen.Count, updated, removed);
	}

	public async Task<(int Processed, int Errors)> ScanDirectoryAsync(string sourcePath, ConcurrentBag<Uri> foundFiles)
	{
		if (!Directory.Exists(sourcePath)) {
			_logger?.LogWarning("Directory not found: {sourcePath}", sourcePath);
			return (0, 0);
		}

		_logger?.LogInformation("Starting scan of directory: {sourcePath}", sourcePath);

		var total  = 0;
		var errors = 0;
		var files  = Directory
			.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
			.Where(file => _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));

		using var context = await _contextFactory.CreateDbContextAsync();
		
		await Task.WhenAll(files.Select(async filePath => {
			try {
				Interlocked.Increment(ref total);
				await ProcessFileAsync(context, sourcePath, filePath, foundFiles);
			} catch (Exception ex) {
				Interlocked.Increment(ref errors);
				_logger?.LogError(ex, "Error processing file: {FilePath}", filePath);
			}
		}));

		await context.SaveChangesAsync();

		return (total, errors);
	}

	private async Task ProcessFileAsync(LibraryContext context, string sourcePath, string filePath, ConcurrentBag<Uri> foundFiles)
	{
		var fileInfo = new FileInfo(filePath);

		if (!fileInfo.Exists) {
			return;
		}

		var hashTask = CalculateFileHashAsync(filePath);
		var songPath = Path.GetRelativePath(sourcePath, Path.GetFullPath(filePath));
		var source   = default(Source?);
		var song     = default(Song?);

		lock (context) {
			source = context.Sources.SingleOrDefault(s => s.Path == sourcePath);

			if (source == null) {
				throw new InvalidOperationException("The specified source path cannot be found in the database.");
			}

			song = context.Songs.Find(source.Id, songPath);
		}

		// add this file to the list of files we saw
		foundFiles.Add(Song.ConvertToUri(Path.Combine(sourcePath, songPath)));

		// grab the hash, we need it now
		var fileHash = await hashTask;

		// if the song hasn't changed, we're done here
		if (song != null && song.FileHash == fileHash) {
			return;
		}

		try {
			using var tagFile = TagLib.File.Create(filePath);

			var updatedSong = new Song {
				SourceId     = source.Id,
				RelativePath = songPath,
				FileHash     = fileHash,
				Artist       = CleanMetadata(tagFile.Tag.FirstPerformer) ?? "Unknown Artist",
				Album        = CleanMetadata(tagFile.Tag.Album) ?? "Unknown Album",
				Title        = CleanMetadata(tagFile.Tag.Title) ?? Path.GetFileNameWithoutExtension(songPath),
				TrackNumber  = (int)tagFile.Tag.Track,
				TrackTotal   = (int)tagFile.Tag.TrackCount,
				Duration     = tagFile.Properties.Duration,
				FileModified = fileInfo.LastWriteTime,
				FileSize     = fileInfo.Length
			};

			// update (or add) the song to the database
			if (song != null) {
				context.Entry(song).CurrentValues.SetValues(updatedSong);
			} else {
				context.Songs.Add(updatedSong);
			}
		} catch (Exception ex) {
			_logger?.LogError(ex, "Failed to read metadata for file: {FilePath}", songPath);
		}
	}

	private static string? CleanMetadata(string? metadata)
	{
		return string.IsNullOrWhiteSpace(metadata) ? null : metadata.Trim();
	}

	private static async Task<string> CalculateFileHashAsync(string filePath)
	{
		using var stream = File.OpenRead(filePath);
		using var sha256 = SHA256.Create();

		var hash = await sha256.ComputeHashAsync(stream);
		return Convert.ToHexStringLower(hash);
	}

	public async Task<List<(string Artist, string Album)>> GetAlbums()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.OrderBy(a => a.Artist)
			.ThenBy(a => a.Album)
			.Select(s => new { s.Artist, s.Album })
			.Distinct()
			.Select(x => ValueTuple.Create(x.Artist, x.Album))
			.ToListAsync();
	}

	public async Task<List<Song>> GetAlbum(string artist, string album)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.Where(song => song.Artist == artist && song.Album == album)
			.ToListAsync();
	}

	public async Task<List<Song>> SearchLibraryAsync(string query)
	{
		if (string.IsNullOrWhiteSpace(query)) {
			throw new ArgumentException("Query parameter is required", nameof(query));
		}

		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs.Where(song =>
			song.Artist.Contains(query) ||
			song.Album.Contains(query) ||
			song.Title.Contains(query)
		).ToListAsync();
	}

	public async Task<(long Songs, long Artists, long Albums)> GetLibraryStatsAsync()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var totalSongs   = await context.Songs.CountAsync();
		var totalArtists = await context.Songs.Select(s => s.Artist).Distinct().CountAsync();
		var totalAlbums  = await context.Songs.Select(s => s.Album).Distinct().CountAsync();

		return (totalSongs, totalArtists, totalAlbums);
	}

	public async Task<List<Song>> FindSongsAsync(string? artist, string? album, string? title)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var query = context.Songs.AsQueryable();

		// Apply filters based on provided criteria
		if (!string.IsNullOrWhiteSpace(artist)) {
			query = query.Where(s => s.Artist.Contains(artist));
		}

		if (!string.IsNullOrWhiteSpace(album)) {
			query = query.Where(s => s.Album.Contains(album));
		}

		if (!string.IsNullOrWhiteSpace(title)) {
			query = query.Where(s => s.Title.Contains(title));
		}

		return await query.ToListAsync();
	}
}
