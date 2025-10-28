using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SongServer.Services.Library;
using SongServer.Services.Smb;

namespace SongServer.Handlers;

internal class LibraryHandlers
{
	private LibraryHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("library/");

		group.MapGet("/artists", GetArtists)
			.WithName("GetAllArtists")
			.WithSummary("Get all albums in the library");

		group.MapGet("/album/{artist}/{album}", GetAlbum)
			.WithName("GetAlbum")
			.WithSummary("Gets an album from the library");

		group.MapGet("/search", SearchLibraryAsync)
			.WithName("SearchLibrary")
			.WithSummary("Search the library");

		group.MapPost("/refresh", RefreshAsync)
			.WithName("RefreshLibrary")
			.WithSummary("Scan directories for music files");

		group.MapGet("/stats", GetLibraryStatsAsync)
			.WithName("GetLibraryStats")
			.WithSummary("Get library statistics");

		group.MapGet("/stream/{songInfo}", StreamSongAsync)
			.WithName("StreamSong")
			.WithSummary("Stream a song from the library (compatible with MPD curl plugin)");
	}

	public static async Task<Results<Ok<IEnumerable<ArtistInfo>>, ProblemHttpResult>> GetArtists(LibraryService libraryService)
	{
		try {
			var artists = await libraryService.GetArtists();
			return TypedResults.Ok(artists.Select(a => new ArtistInfo(a.Artist, a.Albums)));
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error retrieving albums: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<IEnumerable<SongMetadata>>, ProblemHttpResult>> GetAlbum(string artist, string album, LibraryService libraryService)
	{
		try {
			var songs = await libraryService.GetAlbum(artist, album);
			var payload = songs.Select(s => new SongMetadata(s.AlbumArtists, s.Album, s.Title, s.TrackNumber, s.TrackCount, null, s.Duration));
			return TypedResults.Ok(payload);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error retrieving songs by artist and album: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<IEnumerable<SongMetadata>>, BadRequest<string>, ProblemHttpResult>> SearchLibraryAsync(string query, LibraryService libraryService)
	{
		try {
			var results = await libraryService.SearchLibrary(query);
			var payload = results.Select(s => new SongMetadata(s.AlbumArtists, s.Album, s.Title, s.TrackNumber, s.TrackCount, null, s.Duration));
			return TypedResults.Ok(payload);
		} catch (ArgumentException ex) {
			return TypedResults.BadRequest(ex.Message);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error searching library: " + ex.Message });
		}
	}

	public static Results<Ok<OperationResult>, ProblemHttpResult> RefreshAsync(LibraryService libraryService, ILogger<LibraryService> logger)
	{
		try {
			_ = Task.Run(libraryService.RefreshAsync);
			return TypedResults.Ok(new OperationResult("Library scan initiated successfully"));
		} catch (Exception ex) {
			logger.LogError(ex, "Error scanning library");
			return TypedResults.Problem(new() { Title = "Error scanning library: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<LibraryStats>, ProblemHttpResult>> GetLibraryStatsAsync(LibraryService libraryService)
	{
		try {
			var stats = await libraryService.GetLibraryStatsAsync();
			var payload = new LibraryStats(stats.Songs, stats.Artists, stats.Albums);
			return TypedResults.Ok(payload);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error retrieving library stats: " + ex.Message });
		}
	}

	public static async Task StreamSongAsync(string songInfo, HttpContext context, LibraryContext library, SmbFileClient smbClient, ILogger<LibraryService> logger)
	{
		try {
			// Decode base64 JSON string
			byte[] decodedBytes;
			try {
				decodedBytes = Convert.FromBase64String(songInfo);
			} catch (FormatException) {
				context.Response.StatusCode = StatusCodes.Status400BadRequest;
				await context.Response.WriteAsJsonAsync(new { error = "Invalid base64 encoding in songInfo parameter" });
				return;
			}

			var jsonString = System.Text.Encoding.UTF8.GetString(decodedBytes);

			// Deserialize JSON to SongReference
			SongReference? songReference;
			try {
				songReference = JsonSerializer.Deserialize<SongReference>(jsonString);
			} catch (JsonException) {
				context.Response.StatusCode = StatusCodes.Status400BadRequest;
				await context.Response.WriteAsJsonAsync(new { error = "Invalid JSON in songInfo parameter" });
				return;
			}

			if (songReference == null || songReference.SourceId == default || string.IsNullOrEmpty(songReference.RelativePath)) {
				context.Response.StatusCode = StatusCodes.Status400BadRequest;
				await context.Response.WriteAsJsonAsync(new { error = "songInfo must contain valid 'sourcePath' and 'relativePath' properties" });
				return;
			}

			// Get the song from the library service
			var song = library.Songs.FirstOrDefault(s => s.SourceId == songReference.SourceId && s.RelativePath == songReference.RelativePath);

			if (song == null) {
				logger.LogWarning("Song not found: sourceId={sourceId}, relativePath={relativePath}", songReference.SourceId, songReference.RelativePath);
				context.Response.StatusCode = StatusCodes.Status404NotFound;
				return;
			}

			//await StreamFilesystemFile(song, context.Response, logger);
			await StreamSmbFile(song, smbClient, context.Response, logger);
		} catch (Exception ex) {
			logger.LogError(ex, "Error streaming song");
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			await context.Response.WriteAsJsonAsync(new { error = "Error streaming song: " + ex.Message });
		}
	}

	private static async Task StreamFilesystemFile(Song song, HttpResponse response, ILogger logger)
	{
		var filePath = Path.Combine(song.SourcePath, song.RelativePath);

		// Check if file exists
		if (!File.Exists(filePath)) {
			logger.LogError("File not found for song: {filePath}", filePath);
			response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		// Get file info
		var fileInfo = new FileInfo(filePath);

		// Determine content type based on file extension
		var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch {
			".mp3" => "audio/mpeg",
			".flac" => "audio/flac",
			".opus" => "audio/opus",
			".m4a" => "audio/mp4",
			_ => "application/octet-stream",
		};

		// Set response headers for streaming
		response.ContentType = contentType;
		response.ContentLength = fileInfo.Length;
		response.Headers.AcceptRanges = "bytes";
		response.Headers[HeaderNames.ETag] = new EntityTagHeaderValue($"\"{song.FileHash}\"", isWeak: true).ToString();
		response.Headers[HeaderNames.LastModified] = new DateTimeOffset(song.FileModified).ToString("R");
		response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";

		// Stream the file
		await response.SendFileAsync(filePath);
	}

	private static async Task StreamSmbFile(Song song, SmbFileClient smb, HttpResponse response, ILogger logger)
	{
		var filePath = Path.Combine(song.SourcePath, song.RelativePath);

		try {
			// Open the file from SMB
			var (store, handle) = smb.OpenRead(filePath);

			// Get file information to determine length
			var fistat = store.GetFileInformation(out var fileInfo, handle, SMBLibrary.FileInformationClass.FileStandardInformation);
			
			switch (fistat) {
				case SMBLibrary.NTStatus.STATUS_SUCCESS:
					break;

				case SMBLibrary.NTStatus.STATUS_NO_SUCH_FILE:
					logger.LogError("File not found for song: {filePath}", filePath);
					response.StatusCode = StatusCodes.Status404NotFound;
					return;

				default:
					response.StatusCode = StatusCodes.Status500InternalServerError;
					await response.WriteAsJsonAsync(new ProblemDetails() {
						Status = response.StatusCode,
						Detail = $"An error occurred reading file from SMB server: {fistat}",
					});
					return;
			}

			// Get file extension to determine content type
			var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch {
				".mp3" => "audio/mpeg",
				".flac" => "audio/flac",
				".opus" => "audio/opus",
				".m4a" => "audio/mp4",
				_ => "application/octet-stream",
			};

			var stdInfo = fileInfo as SMBLibrary.FileStandardInformation ?? throw new InvalidOperationException("Wrong class returned from GetFileInformation()");

			// Set response headers for streaming
			response.ContentType = contentType;
			response.ContentLength = stdInfo.EndOfFile;
			response.Headers.AcceptRanges = "bytes";
			response.Headers[HeaderNames.ETag] = new EntityTagHeaderValue($"\"{song.FileHash}\"", isWeak: true).ToString();
			response.Headers[HeaderNames.LastModified] = new DateTimeOffset(song.FileModified).ToString("R");
			response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";

			logger.LogInformation("Preparing to stream song of type {type} and length {length}", contentType, stdInfo.EndOfFile);

			// Stream the file in chunks
			const int bufferSize = 65536; // 64KB chunks
			long position = 0;

			while (position < stdInfo.EndOfFile) {
				var readSize = (int)Math.Min(bufferSize, stdInfo.EndOfFile - position);
				var readStatus = store.ReadFile(out var data, handle, position, readSize);

				if (readStatus != SMBLibrary.NTStatus.STATUS_SUCCESS) {
					throw new IOException($"Failed to read file at position {position}: {readStatus}");
				}

				await response.Body.WriteAsync(data);
				position += data.Length;
			}
		} catch (Exception ex) {
			if (!response.HasStarted) {
				response.StatusCode = StatusCodes.Status500InternalServerError;
				await response.WriteAsJsonAsync(new { error = "Error streaming SMB file: " + ex.Message });
			} else {
				throw;
			}
		}
	}
}
