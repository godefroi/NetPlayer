using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace SongServer.Services.Library;

public class LibraryService(IDbContextFactory<LibraryContext> contextFactory, ILogger<LibraryService>? logger = null) : IDisposable
{
	private readonly static FrozenSet<string> _extensions = [".mp3", ".opus", ".flac", ".m4a"];

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
				// delete from albums;delete from artists;delete from album_artists;delete from songs;
				libraryPaths = await context.Sources.Select(s => s.Path).ToListAsync();
			}

			// scan the source paths
			foreach (var p in libraryPaths) {
				//var (tp, te) = await ScanDirectoryAsync(p, seen);
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

			// remove any albums that no longer exist
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				context.Albums.RemoveRange(context.Albums.Where(a => a.Songs.Count() == 0));
				remAlbum = await context.SaveChangesAsync();
				//remAlbum = await context.Albums.Where(a => a.Songs.Count() == 0).ExecuteDeleteAsync();
			}

			// remove any artists for which we have no more albums
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				context.Artists.RemoveRange(context.Artists.Where(a => a.Albums.Count() == 0));
				remArtist = await context.SaveChangesAsync();
				//remArtist = await context.Artists.Where(a => a.Albums.Count() == 0).ExecuteDeleteAsync();
			}

			// remove any playlist items that no longer exist in the library
			using (var context = await _contextFactory.CreateDbContextAsync()) {
				
			}
		} finally {
			_scanSemaphore.Release();
		}

		_logger?.LogInformation("Refresh complete in {dur}; {processed} processed, {updated} added/updated, {removed} removed. {remAlbum} albums and {remArtist} artists pruned.", Stopwatch.GetElapsedTime(start), seen.Count, updated, removed, remAlbum, remArtist);
	}

	public async Task<(int Processed, int Errors)> ScanDirectoryAsync(string sourcePath, HashSet<Uri> foundFiles)
	{
		if (!Directory.Exists(sourcePath)) {
			_logger?.LogWarning("Directory not found: {sourcePath}", sourcePath);
			return (0, 0);
		}

		_logger?.LogInformation("Starting scan of {sourcePath}", sourcePath);

		var total  = 0;
		var errors = 0;
		var changes = 0;
		var files  = Directory
			.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
			.Where(file => _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
		
		foreach (var file in files) {
			try {
				total++;
				changes += await ProcessFileAsync(sourcePath, file, foundFiles);
			} catch (Exception ex) {
				errors++;
				_logger?.LogError(ex, "Error processing file: {filePath}", file);
			}
		}

		_logger?.LogInformation("Scan complete for {path} ({processed} processed, {errors} errors, {changes} changes saved)", sourcePath, total, errors, changes);

		return (total, errors);
	}

	private async Task<(int Processed, int Errors)> ProcessSourceDirectory(string sourcePath, HashSet<Uri> foundFiles)
	{
		(string FileName, string Hash, TagLib.Tag Tag, TimeSpan Duration)[]? files;

		_logger?.LogInformation("Starting scan of {sourcePath}", sourcePath);

		var changes     = 0;
		var errors      = 0;
		var total       = 0;
		var directories = Directory
			.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
			.Where(file => _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
			.GroupBy(f => Path.GetDirectoryName(f));

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

		List<string> artists;

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

		if (tag.AlbumArtists.Length > 0) {
			artists = [.. tag.AlbumArtists.Select(a => CleanMetadata(a) ?? "Unknown Artist").Distinct()];
		} else if (tag.Performers.Length > 0) {
			artists = [.. tag.Performers.Select(a => CleanMetadata(a) ?? "Unknown Artist").Distinct()];
		} else {
			artists = ["Unknown Artist"];
		}

		// step one, we need the album
		var album = await GetAlbum(context, CleanMetadata(tag.Album) ?? "Unknown Album");

		// ensure the album has the artists we want
		// first, add any that aren't there
		foreach (var tagArtist in artists.Where(ta => !album.Artists.Any(a => a.Name == ta))) {
			album.Artists.Add(await GetArtist(context, tagArtist));
		}

		// then, remove any extra
		foreach (var albumArtist in album.Artists.Where(aa => !artists.Any(a => a == aa.Name)).ToList()) {
			album.Artists.Remove(albumArtist);
		}

		if (song != null) {
			// we have the song, but something changed
			song.FileHash     = fileHash;
			song.Album        = album;
			song.Title        = CleanMetadata(tag.Title) ?? Path.GetFileNameWithoutExtension(songPath);
			song.TrackNumber  = (int)tag.Track;
			song.TrackTotal   = (int)tag.TrackCount;
			song.Duration     = duration;
			song.FileModified = fileInfo.LastWriteTime;
			song.FileSize     = fileInfo.Length;
		} else {
			// new song... load 'er up
			song = new Song {
				SourceId     = source.Id,
				RelativePath = songPath,
				FileHash     = fileHash,
				Album        = await GetAlbum(context, CleanMetadata(tag.Album) ?? "Unknown Album"),
				Title        = CleanMetadata(tag.Title) ?? Path.GetFileNameWithoutExtension(songPath),
				TrackNumber  = (int)tag.Track,
				TrackTotal   = (int)tag.TrackCount,
				Duration     = duration,
				FileModified = fileInfo.LastWriteTime,
				FileSize     = fileInfo.Length
			};

			context.Songs.Add(song);
		}

		// update (or add) the song to the database
		return await context.SaveChangesAsync();
	}

	private async Task<int> ProcessFileAsync(string sourcePath, string filePath, HashSet<Uri> foundFiles)
	{
		var fileInfo = new FileInfo(filePath);

		if (!fileInfo.Exists) {
			return 0;
		}

		using var context = await _contextFactory.CreateDbContextAsync();

		var hashTask = CalculateFileHashAsync(filePath);
		var songPath = Path.GetRelativePath(sourcePath, Path.GetFullPath(filePath));
		var source   = context.Sources.SingleOrDefault(s => s.Path == sourcePath) ?? throw new InvalidOperationException("The specified source path cannot be found in the database.");
		var song     = context.Songs.Find(source.Id, songPath);

		// add this file to the list of files we saw
		foundFiles.Add(Song.ConvertToUri(Path.Combine(sourcePath, songPath)));

		// grab the hash, we need it now
		var fileHash = await hashTask;

		// if the song hasn't changed, we're done here
		if (song != null && song.FileHash == fileHash) {
			return 0;
		}

		using var tagFile = TagLib.File.Create(filePath);

		var updatedSong = new Song {
			SourceId     = source.Id,
			RelativePath = songPath,
			FileHash     = fileHash,
			Album        = await GetAlbum(context, CleanMetadata(tagFile.Tag.Album) ?? "Unknown Album"),
			Title        = CleanMetadata(tagFile.Tag.Title) ?? Path.GetFileNameWithoutExtension(songPath),
			TrackNumber  = (int)tagFile.Tag.Track,
			TrackTotal   = (int)tagFile.Tag.TrackCount,
			Duration     = tagFile.Properties.Duration,
			FileModified = fileInfo.LastWriteTime,
			FileSize     = fileInfo.Length
		};

		IEnumerable<string> artists;

		if (tagFile.Tag.AlbumArtists.Length > 0) {
			artists = tagFile.Tag.AlbumArtists;
		} else if (tagFile.Tag.Performers.Length > 0) {
			artists = tagFile.Tag.Performers;
		} else {
			artists = ["Unknown Artist"];
		}

		foreach (var artist in artists) {
			var cleanArtist = CleanMetadata(artist);

			if (string.IsNullOrWhiteSpace(cleanArtist)) {
				continue;
			}

			var dbArtist = await GetArtist(context, cleanArtist);

			if (!updatedSong.Album.Artists.Contains(dbArtist)) {
				updatedSong.Album.Artists.Add(dbArtist);
			}
		}

		// update (or add) the song to the database
		if (song != null) {
			context.Entry(song).CurrentValues.SetValues(updatedSong);
		} else {
			context.Songs.Add(updatedSong);
		}

		return await context.SaveChangesAsync();
	}

	private static async Task<(string FileName, string Hash, TagLib.Tag Tag, TimeSpan Duration)> ReadFile(string sourcePath, string filePath)
	{
		var hashTask = CalculateFileHashAsync(filePath);

		using var tagFile = TagLib.File.Create(filePath);

		return (filePath, await hashTask, tagFile.Tag, tagFile.Properties.Duration);
	}

	private static async Task<Artist> GetArtist(LibraryContext context, string artist)
	{
		var entity = context.Artists.Local.SingleOrDefault(a => a.Name == artist)
			?? await context.Artists.SingleOrDefaultAsync(a => a.Name == artist);

		if (entity == null) {
			entity = new Artist() {
				Name = artist
			};

			context.Artists.Add(entity);
		}

		return entity;
	}

	private static async Task<Album> GetAlbum(LibraryContext context, string album)
	{
		var entity = context.Albums.Local.SingleOrDefault(a => a.Title == album)
			?? await context.Albums.SingleOrDefaultAsync(a => a.Title == album);

		if (entity == null) {
			entity = new Album() {
				Title = album
			};

			context.Albums.Add(entity);
		}

		return entity;
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

	public async Task<List<Album>> GetAlbums()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Albums
			.Include(a => a.Artists)
			.OrderBy(a => a.Title)
			.AsNoTracking()
			.ToListAsync();
	}

	public async Task<List<Song>> GetAlbum(string artist, string album)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.Where(song => song.Album.Artists.Any(a => a.Name == artist) && song.Album.Title == album)
			.ToListAsync();
	}

	public async Task<List<Song>> SearchLibraryAsync(string query)
	{
		if (string.IsNullOrWhiteSpace(query)) {
			throw new ArgumentException("Query parameter is required", nameof(query));
		}

		using var context = await _contextFactory.CreateDbContextAsync();

		return await context.Songs
			.AsNoTracking()
			.Where(song =>
				song.Artists.Any(a => EF.Functions.Like(a.Name, $"%{query}%")) ||
				EF.Functions.Like(song.Album.Title, $"%{query}%") ||
				EF.Functions.Like(song.Title, $"%{query}%")
			).ToListAsync();
	}

	public async Task<(long Songs, long Artists, long Albums)> GetLibraryStatsAsync()
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var totalSongs   = await context.Songs.CountAsync();
		var totalArtists = await context.Artists.CountAsync();
		var totalAlbums  = await context.Albums.CountAsync();

		return (totalSongs, totalArtists, totalAlbums);
	}

	public async Task<List<Song>> FindSongsAsync(string? artist, string? album, string? title)
	{
		using var context = await _contextFactory.CreateDbContextAsync();

		var query = context.Songs.AsQueryable();

		if (!string.IsNullOrWhiteSpace(artist)) {
			query = query.Where(s => s.Artists.Any(a => EF.Functions.Like(a.Name, $"%{artist}%")));
		}

		if (!string.IsNullOrWhiteSpace(album)) {
			query = query.Where(s => EF.Functions.Like(s.Album.Title, $"%{album}%"));
		}

		if (!string.IsNullOrWhiteSpace(title)) {
			query = query.Where(s => s.Title.Contains(title));
		}

		return await query.ToListAsync();
	}
}
