using Microsoft.AspNetCore.Http.HttpResults;
using SongServer.Services.Library;

namespace SongServer.Handlers;

internal class LibraryHandlers
{
	private LibraryHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("library/");

		group.MapGet("/albums", GetAllAlbumsAsync)
			.WithName("GetAllAlbums")
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
	}

	public static async Task<Results<Ok<IEnumerable<AlbumInfo>>, ProblemHttpResult>> GetAllAlbumsAsync(LibraryService libraryService)
	{
		try {
			var albumsByArtist = await libraryService.GetAlbums();
			var payload = albumsByArtist.Select(t => new AlbumInfo(t.Artist, t.Album));
			return TypedResults.Ok(payload);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error retrieving albums by artist: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<IEnumerable<SongMetadata>>, ProblemHttpResult>> GetAlbum(string artist, string album, LibraryService libraryService)
	{
		try {
			var songs = await libraryService.GetAlbum(artist, album);
			var payload = songs.Select(s => new SongMetadata(s.Artist, s.Album, s.Title, s.TrackNumber, s.TrackTotal, null, s.Duration));
			return TypedResults.Ok(payload);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error retrieving songs by artist and album: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<IEnumerable<SongMetadata>>, BadRequest<string>, ProblemHttpResult>> SearchLibraryAsync(string query, LibraryService libraryService)
	{
		try {
			var results = await libraryService.SearchLibraryAsync(query);
			var payload = results.Select(s => new SongMetadata(s.Artist, s.Album, s.Title, s.TrackNumber, s.TrackTotal, null, s.Duration));
			return TypedResults.Ok(payload);
		} catch (ArgumentException ex) {
			return TypedResults.BadRequest(ex.Message);
		} catch (Exception ex) {
			return TypedResults.Problem(new() { Title = "Error searching library: " + ex.Message });
		}
	}

	public static async Task<Results<Ok<OperationResult>, ProblemHttpResult>> RefreshAsync(LibraryService libraryService, ILogger<LibraryService> logger)
	{
		try {
			await libraryService.RefreshAsync();
			return TypedResults.Ok(new OperationResult("Library scan completed successfully"));
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
}
