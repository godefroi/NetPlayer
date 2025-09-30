using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace SongServer.Services.Library;

public class LibraryService
{
	private readonly static FrozenSet<string> _extensions = [".mp3", ".opus", ".flac"];
	private readonly static JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private readonly ConcurrentDictionary<string, LibrarySong> _library = new();
	private readonly string _databasePath;
	private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
	private readonly ILogger<LibraryService>? _logger;
	
	public event EventHandler<ScanProgressEventArgs>? ScanProgress;
	public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

	public LibraryService(string databasePath = "music_library.json", ILogger<LibraryService>? logger = null)
	{
		_databasePath = databasePath;
		_logger = logger;
		LoadDatabase();
	}

	public async Task InitializeAsync(IEnumerable<string> libraryPaths)
	{
		await _scanSemaphore.WaitAsync();
		try
		{
			var scanTasks = libraryPaths.Select(path => ScanDirectoryAsync(path));
			await Task.WhenAll(scanTasks);
			await SaveDatabaseAsync();
		}
		finally
		{
			_scanSemaphore.Release();
		}
	}

	public async Task ScanDirectoryAsync(string directoryPath)
	{
		if (!Directory.Exists(directoryPath))
		{
			_logger?.LogWarning("Directory not found: {DirectoryPath}", directoryPath);
			return;
		}

		_logger?.LogInformation("Starting scan of directory: {DirectoryPath}", directoryPath);

		var supportedFiles = Directory
			.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
			.Where(file => _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
			.ToList();

		var totalFiles = supportedFiles.Count;
		var processedFiles = 0;
		var newFiles = 0;
		var updatedFiles = 0;
		var errors = 0;

		_logger?.LogInformation("Found {TotalFiles} music files to process", totalFiles);

		var tasks = supportedFiles.Select(async filePath =>
		{
			try
			{
				await ProcessFileAsync(filePath);
				
				var processed = Interlocked.Increment(ref processedFiles);
				
				if (processed % 100 == 0 || processed == totalFiles)
				{
					ScanProgress?.Invoke(this, new ScanProgressEventArgs
					{
						TotalFiles = totalFiles,
						ProcessedFiles = processed,
						CurrentFile = filePath
					});
				}
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref errors);
				_logger?.LogError(ex, "Error processing file: {FilePath}", filePath);
			}
		});

		await Task.WhenAll(tasks);

		ScanCompleted?.Invoke(this, new ScanCompletedEventArgs
		{
			TotalFiles = totalFiles,
			NewFiles = newFiles,
			UpdatedFiles = updatedFiles,
			Errors = errors
		});

		_logger?.LogInformation("Scan completed. Processed: {ProcessedFiles}, Errors: {Errors}", processedFiles, errors);
	}

	private async Task ProcessFileAsync(string filePath)
	{
		var fileInfo = new FileInfo(filePath);
		if (!fileInfo.Exists) return;

		var fileHash = await CalculateFileHashAsync(filePath);
		var normalizedPath = Path.GetFullPath(filePath);

		// Check if file already exists and hasn't changed
		if (_library.TryGetValue(normalizedPath, out var existingSong) && 
			existingSong.FileHash == fileHash && 
			existingSong.LastModified == fileInfo.LastWriteTime)
		{
			return; // File unchanged
		}

		try
		{
			using var tagFile = TagLib.File.Create(filePath);
			var tag = tagFile.Tag;

			var song = new LibrarySong
			{
				FilePath = normalizedPath,
				FileHash = fileHash,
				Artist = CleanMetadata(tag.FirstPerformer) ?? "Unknown Artist",
				Album = CleanMetadata(tag.Album) ?? "Unknown Album",
				Title = CleanMetadata(tag.Title) ?? Path.GetFileNameWithoutExtension(filePath),
				TrackNumber = (int)tag.Track,
				TrackTotal = (int)tag.TrackCount,
				Duration = tagFile.Properties.Duration,
				LastModified = fileInfo.LastWriteTime,
				FileSize = fileInfo.Length
			};

			_library[normalizedPath] = song;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to read metadata for file: {FilePath}", filePath);
			
			// Add file with minimal metadata if tag reading fails
			var song = new LibrarySong
			{
				FilePath = normalizedPath,
				FileHash = fileHash,
				Artist = "Unknown Artist",
				Album = "Unknown Album",
				Title = Path.GetFileNameWithoutExtension(filePath),
				TrackNumber = 0,
				TrackTotal = 0,
				Duration = TimeSpan.Zero,
				LastModified = fileInfo.LastWriteTime,
				FileSize = fileInfo.Length
			};

			_library[normalizedPath] = song;
		}
	}

	private static string? CleanMetadata(string? metadata)
	{
		return string.IsNullOrWhiteSpace(metadata) ? null : metadata.Trim();
	}

	private async Task<string> CalculateFileHashAsync(string filePath)
	{
		using var stream = File.OpenRead(filePath);
		using var sha256 = SHA256.Create();
		
		var hash = await sha256.ComputeHashAsync(stream);
		return Convert.ToHexString(hash);
	}

	public async Task SaveDatabaseAsync()
	{
		try
		{
			var songs = _library.Values.OrderBy(s => s.Artist).ThenBy(s => s.Album).ThenBy(s => s.TrackNumber).ToList();
			var json = JsonSerializer.Serialize(songs, JsonOptions);
			
			await File.WriteAllTextAsync(_databasePath, json);
			_logger?.LogInformation("Database saved with {Count} songs", songs.Count);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to save database to {DatabasePath}", _databasePath);
		}
	}

	private void LoadDatabase()
	{
		try
		{
			if (!File.Exists(_databasePath))
			{
				_logger?.LogInformation("Database file not found, starting with empty library");
				return;
			}

			var json = File.ReadAllText(_databasePath);
			var songs = JsonSerializer.Deserialize<List<LibrarySong>>(json, JsonOptions);
			
			if (songs != null)
			{
				_library.Clear();
				foreach (var song in songs)
				{
					_library[song.FilePath] = song;
				}
				
				_logger?.LogInformation("Loaded {Count} songs from database", songs.Count);
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to load database from {DatabasePath}", _databasePath);
		}
	}

	// Search methods
	public IEnumerable<LibrarySong> FindByArtist(string artist, bool exactMatch = false)
	{
		return _library.Values.Where(song => 
			exactMatch 
				? string.Equals(song.Artist, artist, StringComparison.OrdinalIgnoreCase)
				: song.Artist.Contains(artist, StringComparison.OrdinalIgnoreCase)
		);
	}

	public IEnumerable<LibrarySong> FindByAlbum(string album, bool exactMatch = false)
	{
		return _library.Values.Where(song => 
			exactMatch 
				? string.Equals(song.Album, album, StringComparison.OrdinalIgnoreCase)
				: song.Album.Contains(album, StringComparison.OrdinalIgnoreCase)
		);
	}

	public IEnumerable<LibrarySong> FindByTitle(string title, bool exactMatch = false)
	{
		return _library.Values.Where(song => 
			exactMatch 
				? string.Equals(song.Title, title, StringComparison.OrdinalIgnoreCase)
				: song.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
		);
	}

	public IEnumerable<LibrarySong> FindByArtistAndAlbum(string artist, string album)
	{
		return _library.Values.Where(song => 
			song.Artist.Contains(artist, StringComparison.OrdinalIgnoreCase) &&
			song.Album.Contains(album, StringComparison.OrdinalIgnoreCase)
		);
	}

	public IEnumerable<LibrarySong> Search(string query)
	{
		return _library.Values.Where(song =>
			song.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
			song.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
			song.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
		);
	}

	public LibrarySong? FindByPath(string filePath)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		return _library.TryGetValue(normalizedPath, out var song) ? song : null;
	}

	public IEnumerable<LibrarySong> GetAllSongs()
	{
		return _library.Values.OrderBy(s => s.Artist).ThenBy(s => s.Album).ThenBy(s => s.TrackNumber);
	}

	public IEnumerable<string> GetAllArtists()
	{
		return _library.Values.Select(s => s.Artist).Distinct().OrderBy(a => a);
	}

	public IEnumerable<string> GetAllAlbums()
	{
		return _library.Values.Select(s => s.Album).Distinct().OrderBy(a => a);
	}

	public IEnumerable<(string Artist, string Album)> GetAllAlbumsByArtist()
	{
		return _library.Values
			.Select(s => (s.Artist, s.Album))
			.Distinct()
			.OrderBy(a => a.Artist)
			.ThenBy(a => a.Album);
	}

	public int TotalSongs => _library.Count;

	public async Task RefreshAsync()
	{
		// Get all unique directory paths from current library
		var directories = _library.Values
			.Select(s => Path.GetDirectoryName(s.FilePath))
			.Where(d => !string.IsNullOrEmpty(d))
			.Cast<string>()
			.Distinct()
			.ToList();

		if (directories.Count > 0)
		{
			await InitializeAsync(directories);
		}
	}

	public async Task RemoveDeletedFilesAsync()
	{
		var filesToRemove = new List<string>();
		
		foreach (var song in _library.Values)
		{
			if (!File.Exists(song.FilePath))
			{
				filesToRemove.Add(song.FilePath);
			}
		}

		foreach (var filePath in filesToRemove)
		{
			_library.TryRemove(filePath, out _);
		}

		if (filesToRemove.Count > 0)
		{
			_logger?.LogInformation("Removed {Count} deleted files from library", filesToRemove.Count);
			await SaveDatabaseAsync();
		}
	}
}

public class ScanProgressEventArgs : EventArgs
{
	public int TotalFiles { get; set; }
	public int ProcessedFiles { get; set; }
	public string CurrentFile { get; set; } = string.Empty;
	public double ProgressPercentage => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;
}

public class ScanCompletedEventArgs : EventArgs
{
	public int TotalFiles { get; set; }
	public int NewFiles { get; set; }
	public int UpdatedFiles { get; set; }
	public int Errors { get; set; }
}
