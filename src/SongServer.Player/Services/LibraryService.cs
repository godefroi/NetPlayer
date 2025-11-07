using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SongServer.Data;
using SongServer.Services.Smb;

namespace SongServer.Services;

public class LibraryService(IDbContextFactory<LibraryContext> contextFactory, SmbFileClient smbClient, ILogger<LibraryService>? logger = null) : IDisposable
{
	private readonly static FrozenSet<string> _extensions = [".mp3", ".opus", ".flac", ".m4a"];
	private const string _noArtist = "[No Artist]";
	private const string _noAlbum = "[No Album]";

	private readonly SmbFileClient _smbClient = smbClient;
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
					_logger?.LogInformation("Adding source {path}", path);
					context.Set<Source>().Add(new Source { Path = path });
				} else {
					_logger?.LogInformation("Source already exists: {path}", path);
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
		var remAlbum  = 0;
		var remArtist = 0;
		var processed = 0;
		var errors    = 0;
		var seen      = new HashSet<Uri>();
		var start     = Stopwatch.GetTimestamp();

		List<string> libraryPaths;

		await _scanSemaphore.WaitAsync();

		try {
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				// get the list of library paths
				libraryPaths = await context.Sources.Select(s => s.Path).ToListAsync();
			}

			// scan the source paths
			foreach (var p in libraryPaths) {
				var (tp, te) = await ProcessSourceDirectory(p, seen);
				processed += tp;
				errors += te;
			}

			using (var context = await _contextFactory.CreateDbContextAsync()) {
				// now, go through all the files and remove ones we didn't see
				foreach (var f in context.Songs) {
					if (!seen.Contains(f.Url)) {
						context.Songs.Remove(f);
					}
				}

				// save the delete changes
				removed = await context.SaveChangesAsync();
			}

			// remove any playlist items that no longer exist in the library
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				// TODO: do that
			}
		} finally {
			_scanSemaphore.Release();
		}

		_logger?.LogInformation("Refresh complete in {dur}; {processed} processed, {updated} added/updated, {removed} removed. {remAlbum} albums and {remArtist} artists pruned.", Stopwatch.GetElapsedTime(start), seen.Count, updated, removed, remAlbum, remArtist);
	}

	private async Task<(int Processed, int Errors)> ProcessSourceDirectory(string sourcePath, HashSet<Uri> foundFiles)
	{
		(string FileName, string Hash, TagLib.Tag Tag, TimeSpan Duration)[]? files;

		_logger?.LogInformation("Starting scan of {sourcePath}", sourcePath);

		var changes     = 0;
		var errors      = 0;
		var total       = 0;
		var directories = EnumerateFiles(sourcePath)
			.Where(file => _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
			.GroupBy(Path.GetDirectoryName);

		foreach (var dir in directories) {
			// read the files in parallel
			try {
				files = await Task.WhenAll(dir.Select(f => ReadFile(sourcePath, f)));
			} catch (AggregateException ae) {
				foreach (var e in ae.InnerExceptions) {
					_logger?.LogError(e, "Error occurred reading file");
				}

				errors += 1;
				continue;
			}

			// then process them one-at-a-time
			foreach (var (fileName, hash, tag, duration) in files) {
				try {
					changes += await ProcessFile(sourcePath, fileName, hash, tag, duration, foundFiles);
				} catch (Exception e) {
					errors++;
					_logger?.LogError(e, "Error occurred while processing file {sourcePath}", fileName);
				}
			}

			// count the files
			total += files.Length;
		}

		_logger?.LogInformation("Scan complete for {path} ({processed} processed, {errors} errors, {changes} changes saved)", sourcePath, total, errors, changes);

		return (changes, errors);
	}

	private async Task<int> ProcessFile(string sourcePath, string filePath, string fileHash, TagLib.Tag tag, TimeSpan duration, HashSet<Uri> foundFiles)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var fileInfo = new FileInfo(filePath);
		var songPath = Path.GetRelativePath(sourcePath, Path.GetFullPath(filePath));
		var source   = context.Sources.SingleOrDefault(s => s.Path == sourcePath) ?? throw new InvalidOperationException("The specified source path cannot be found in the database.");
		var song     = context.Songs.Find(source.Id, songPath);

		// add this file to the list of files we saw
		foundFiles.Add(Song.ConvertToUri(Path.Combine(sourcePath, songPath)));

		if (song != null && song.FileHash == fileHash) {
			// the song hasn't changed, we're done here
			return 0;
		}

		var album            = Clean(tag.Album);
		var albumSort        = Clean(tag.AlbumSort);
		var albumArtists     = Clean(tag.AlbumArtists);
		var albumArtistsSort = Clean(tag.AlbumArtistsSort);
		var performers       = Clean(tag.Performers);
		var performersSort   = Clean(tag.PerformersSort);

		if (song != null) {
			// we have the song, but something changed
			song.FileHash         = fileHash;
			song.FileModified     = fileInfo.LastWriteTime;
			song.FileSize         = fileInfo.Length;
			song.Album            = album;
			song.AlbumSort        = albumSort;
			song.AlbumArtists     = albumArtists;
			song.AlbumArtistsSort = albumArtistsSort;
			song.Performers       = performers;
			song.PerformersSort   = performersSort;
			song.Title            = Clean(tag.Title) ?? Path.GetFileNameWithoutExtension(songPath);
			song.TrackNumber      = tag.Track == 0 ? null : (int)tag.Track;
			song.TrackCount       = tag.TrackCount == 0 ? null : (int)tag.TrackCount;
			song.DiscNumber       = tag.Disc == 0 ? null : (int)tag.Disc;
			song.DiscCount        = tag.DiscCount == 0 ? null : (int)tag.DiscCount;

			song.Duration         = duration;
		} else {
			// new song... load 'er up
			song = new Song {
				SourceId         = source.Id,
				RelativePath     = songPath,
				FileHash         = fileHash,
				FileModified     = fileInfo.LastWriteTime,
				FileSize         = fileInfo.Length,
				Album            = album,
				AlbumSort        = albumSort,
				AlbumArtists     = albumArtists,
				AlbumArtistsSort = albumArtistsSort,
				Performers       = performers,
				PerformersSort   = performersSort,
				Title            = Clean(tag.Title) ?? Path.GetFileNameWithoutExtension(songPath),
				TrackNumber      = tag.Track == 0 ? null : (int)tag.Track,
				TrackCount       = tag.TrackCount == 0 ? null : (int)tag.TrackCount,
				DiscNumber       = tag.Disc == 0 ? null : (int)tag.Disc,
				DiscCount        = tag.DiscCount == 0 ? null : (int)tag.DiscCount,
				Duration         = duration,
			};

			context.Songs.Add(song);
		}

		// update (or add) the song to the database
		return await context.SaveChangesAsync();
	}

	private async Task<(string FileName, string Hash, TagLib.Tag Tag, TimeSpan Duration)> ReadFile(string sourcePath, string filePath)
	{
		using var sha256 = SHA256.Create();

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && sourcePath.StartsWith(@"\\")) {
			// read the file via SMB
			var (store, handle) = _smbClient.OpenRead(sourcePath);
			using var stream = new SmbFileStream(store, handle);
			using var tagFile = TagLib.File.Create(new SmbFileAbstraction(store, handle));
			var hashTask = sha256.ComputeHashAsync(stream);

			return (filePath, Convert.ToHexStringLower(await hashTask), tagFile.Tag, tagFile.Properties.Duration);
		} else {
			// not linux, or not a UNC path
			using var stream  = File.OpenRead(filePath);
			var hashTask = sha256.ComputeHashAsync(stream);
			using var tagFile = TagLib.File.Create(filePath);

			return (filePath, Convert.ToHexStringLower(await hashTask), tagFile.Tag, tagFile.Properties.Duration);
		}
	}

	private IEnumerable<string> EnumerateFiles(string sourcePath)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && sourcePath.StartsWith(@"\\")) {
			return _smbClient.EnumerateFiles(sourcePath);
		} else {
			return Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories);
		}
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
