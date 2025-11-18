using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SongServer.Data;

namespace SongServer.Services.Library;

// prepare for the uri-pocalypse:
//   update sources set path = replace(replace(path, '\\', 'smb://'), '\', '/');
//   update sources set path = path || '/';
//   update songs set path = replace(path, '\', '/');
//   update playlist set relative_path = replace(relative_path, '\', '/');

public class LibraryService(IDbContextFactory<LibraryContext> contextFactory, FileInformationRetriever infoRetriever, ILogger<LibraryService>? logger = null) : IDisposable
{
	private readonly static FrozenSet<string> _extensions = [".mp3", ".opus", ".flac", ".m4a"];
	private const string _noArtist = "[No Artist]";
	private const string _noAlbum = "[No Album]";

	private readonly IDbContextFactory<LibraryContext> _contextFactory = contextFactory;
	private readonly FileInformationRetriever _infoRetriever = infoRetriever;
	private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
	private readonly ILogger<LibraryService>? _logger = logger;

	public void Dispose()
	{
		_scanSemaphore.Dispose();
		GC.SuppressFinalize(this);
	}

	public async Task InitializeAsync(IEnumerable<Uri> sourceUris)
	{
		await _scanSemaphore.WaitAsync();

		try {
			using var context = await _contextFactory.CreateDbContextAsync();
			
			// ensure the database is created
			await context.Database.EnsureCreatedAsync();

			// add sources to database if they don't exist
			foreach (var uri in sourceUris) {
				var existingSource = await context.Sources.SingleOrDefaultAsync(s => s.Path == uri);

				if (existingSource == null) {
					_logger?.LogInformation("Adding source {path}", uri);
					context.Set<Source>().Add(new Source { Path = uri });
				} else {
					_logger?.LogInformation("Source already exists: {path}", uri);
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

	public async Task<Song?> GetSong(SongReference songReference, CancellationToken cancellationToken = default)
	{
		using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

		return await ctx.Songs
			.AsNoTracking()
			.Include(s => s.Source)
			.SingleOrDefaultAsync(s => s.SourceId == songReference.SourceId && s.RelativePath == songReference.RelativePath, cancellationToken);
	}

	public async Task<List<(string Artist, List<string> Albums)>> GetArtists()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return [.. context.Songs
			.Select(s => new { s.Album, s.Performers, s.AlbumArtists })
			// invert in-memory to avoid SQL APPLY (not supported in SQLite)
			.AsEnumerable()
			.SelectMany(s => {
				var allArtists = Enumerable.Empty<string>();

				if (s.Performers != null) {
					allArtists = allArtists.Concat(s.Performers);
				}

				if (s.AlbumArtists != null) {
					allArtists = allArtists.Concat(s.AlbumArtists);
				}

				return allArtists
					.Distinct()
					.DefaultIfEmpty(_noArtist);
			}, (song, artist) => new { Artist = artist, Album = song.Album ?? _noAlbum })
			.Distinct()
			.GroupBy(x => x.Artist)
			.Select(g => (Artist: g.Key, Albums: g.Select(x => x.Album).Distinct().ToList()))];
	}

	public async Task<List<Song>> GetAlbum(string? artist, string? album)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.AsNoTracking()
			.Where(song =>
				((artist == null || artist == _noArtist) && song.AlbumArtists == null && song.Performers == null) ||
				(song.AlbumArtists != null && song.AlbumArtists.Any(a => a == artist)) ||
				(song.Performers != null && song.Performers.Any(p => p == artist))
			)
			.Where(song => ((album == null || album == _noAlbum) && song.Album == null) || song.Album == album)
			.ToListAsync();
	}

	public async Task<List<Song>> SearchLibrary(string query)
	{
		if (string.IsNullOrWhiteSpace(query)) {
			throw new ArgumentException("Query parameter is required", nameof(query));
		}

		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.AsNoTracking()
			.Where(song =>
				(song.AlbumArtists != null && song.AlbumArtists.Any(a => EF.Functions.Like(a, $"%{query}%"))) ||
				(song.Performers != null && song.Performers.Any(p => EF.Functions.Like(p, $"%{query}%"))) ||
				EF.Functions.Like(song.Album, $"%{query}%") ||
				EF.Functions.Like(song.Title, $"%{query}%")
			).ToListAsync();
	}

	public async Task<List<Song>> SearchLibrary(string? artist, string? album, string? title)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var query = context.Songs.AsQueryable();

		if (!string.IsNullOrWhiteSpace(artist)) {
			query = query.Where(s =>
				(s.AlbumArtists != null && s.AlbumArtists.Any(a => EF.Functions.Like(a, $"%{artist}%"))) ||
				(s.Performers != null && s.Performers.Any(a => EF.Functions.Like(a, $"%{artist}%")))
			);
		}

		if (!string.IsNullOrWhiteSpace(album)) {
			query = query.Where(s => EF.Functions.Like(s.Album, $"%{album}%"));
		}

		if (!string.IsNullOrWhiteSpace(title)) {
			query = query.Where(s => s.Title.Contains(title));
		}

		return await query.ToListAsync();
	}

	public async Task<(long Songs, long Artists, long Albums)> GetLibraryStatsAsync()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var totalSongs   = await context.Songs.CountAsync();
		var totalArtists = await context.Songs.Where(s => s.AlbumArtists != null).SelectMany(s => s.AlbumArtists!).Distinct().CountAsync();
		var totalAlbums  = await context.Songs.Select(s => s.Album).Distinct().CountAsync();

		return (totalSongs, totalArtists, totalAlbums);
	}

	public async Task RefreshAsync()
	{
		var updated   = 0;
		var removed   = 0;
		var processed = 0;
		var errors    = 0;
		var start     = Stopwatch.GetTimestamp();

		List<Source> sources;

		await _scanSemaphore.WaitAsync();

		try {
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				// get the list of library paths
				sources = await context.Sources.AsNoTracking().ToListAsync();
			}

			// scan the source paths
			foreach (var p in sources) {
				var (tp, te) = await ProcessSourceDirectory(p.Path, p.Id);
				processed += tp;
				errors += te;
			}

			// remove any playlist items that no longer exist in the library
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				// TODO: do that
				// TODO: there's foreign keys with cascade deletes...
			}
		} finally {
			_scanSemaphore.Release();
		}

		_logger?.LogInformation("Refresh complete in {dur}; {processed} processed, {updated} added/updated.", Stopwatch.GetElapsedTime(start), updated, removed);
	}

	private async Task<(int Processed, int Errors)> ProcessSourceDirectory(Uri sourceUri, long sourceId)
	{
		_logger?.LogInformation("Starting scan of {sourcePath}", sourceUri);

		var seenFiles = new HashSet<Uri>();
		var changes   = 0;
		var errors    = 0;
		var removed   = 0;
		var files     = _infoRetriever.EnumerateFiles(sourceUri)
			.Where(file => _extensions.Contains(Path.GetExtension(file.LocalPath), StringComparer.OrdinalIgnoreCase));

		foreach (var file in files) {
			FileRecord fr;

			// record that we saw the file
			seenFiles.Add(file);

			// read all the info about the file
			try {
				fr = await _infoRetriever.ReadFileInfo(file);
			} catch (Exception e) {
				_logger?.LogError(e, "Error reading file information for file {file}", file);
				errors++;
				continue;
			}

			// then process it into the database
			try {
				changes += await ProcessFile(sourceUri, sourceId, file, fr);
			} catch (Exception e) {
				errors++;
				_logger?.LogError(e, "Error occurred while processing file {file}", file);
			}
		}

		if (errors > 0) {
			_logger?.LogWarning("Errors occurred, skipping removal of stale items from source {source}.", sourceUri);
		} else {
			using var context = await _contextFactory.CreateDbContextAsync();

			// now, go through all the files and remove ones we didn't see
			foreach (var f in context.Songs.Where(s => s.SourceId == sourceId)) {
				if (!seenFiles.Contains(f.Url)) {
					context.Songs.Remove(f);
				}
			}

			// save the delete changes
			removed = await context.SaveChangesAsync();
		}

		_logger?.LogInformation("Scan complete for {path} ({processed} processed, {errors} errors, {removed} removed, {changes} changes saved)", sourceUri, seenFiles.Count, errors, removed, changes);

		return (changes, errors);
	}

	private async Task<int> ProcessFile(Uri sourceUri, long sourceId, Uri fileUri, FileRecord fileInfo)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var relativePath = sourceUri.MakeRelativeUri(fileUri);
		var song         = await context.Songs.FindAsync(sourceId, relativePath);

		if (song != null && song.FileHash == fileInfo.Hash) {
			// the song hasn't changed, we're done here
			return 0;
		}

		var album            = Clean(fileInfo.Tag.Album);
		var albumSort        = Clean(fileInfo.Tag.AlbumSort);
		var albumArtists     = Clean(fileInfo.Tag.AlbumArtists);
		var albumArtistsSort = Clean(fileInfo.Tag.AlbumArtistsSort);
		var performers       = Clean(fileInfo.Tag.Performers);
		var performersSort   = Clean(fileInfo.Tag.PerformersSort);

		if (song != null) {
			// we have the song, but something changed
			song.FileHash         = fileInfo.Hash;
			song.FileModified     = fileInfo.LastWrite;
			song.FileSize         = fileInfo.Length;
			song.Album            = album;
			song.AlbumSort        = albumSort;
			song.AlbumArtists     = albumArtists;
			song.AlbumArtistsSort = albumArtistsSort;
			song.Performers       = performers;
			song.PerformersSort   = performersSort;
			song.Title            = Clean(fileInfo.Tag.Title) ?? Path.GetFileNameWithoutExtension(relativePath.ToString());
			song.TrackNumber      = fileInfo.Tag.Track == 0 ? null : (int)fileInfo.Tag.Track;
			song.TrackCount       = fileInfo.Tag.TrackCount == 0 ? null : (int)fileInfo.Tag.TrackCount;
			song.DiscNumber       = fileInfo.Tag.Disc == 0 ? null : (int)fileInfo.Tag.Disc;
			song.DiscCount        = fileInfo.Tag.DiscCount == 0 ? null : (int)fileInfo.Tag.DiscCount;
			song.Duration         = fileInfo.Duration;
		} else {
			// new song... load 'er up
			song = new Song {
				SourceId         = sourceId,
				RelativePath     = relativePath,
				FileHash         = fileInfo.Hash,
				FileModified     = fileInfo.LastWrite,
				FileSize         = fileInfo.Length,
				Album            = album,
				AlbumSort        = albumSort,
				AlbumArtists     = albumArtists,
				AlbumArtistsSort = albumArtistsSort,
				Performers       = performers,
				PerformersSort   = performersSort,
				Title            = Clean(fileInfo.Tag.Title) ?? Path.GetFileNameWithoutExtension(relativePath.ToString()),
				TrackNumber      = fileInfo.Tag.Track == 0 ? null : (int)fileInfo.Tag.Track,
				TrackCount       = fileInfo.Tag.TrackCount == 0 ? null : (int)fileInfo.Tag.TrackCount,
				DiscNumber       = fileInfo.Tag.Disc == 0 ? null : (int)fileInfo.Tag.Disc,
				DiscCount        = fileInfo.Tag.DiscCount == 0 ? null : (int)fileInfo.Tag.DiscCount,
				Duration         = fileInfo.Duration,
			};

			context.Songs.Add(song);
		}

		// update (or add) the song to the database
		return await context.SaveChangesAsync();
	}

	private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

	private static List<string>? Clean(IEnumerable<string?> strings)
	{
		var list = strings
			.Select(Clean).Where(s => s != null)
			.Cast<string>()
			.ToList();

		return list.Count == 0 ? null : list;
	}
}
