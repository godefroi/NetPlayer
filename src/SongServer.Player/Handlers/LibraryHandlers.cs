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

	public static async Task<IResult> GetAllAlbumsAsync(LibraryService libraryService)
	{
		try {
			var albumsByArtist = await libraryService.GetAlbums();
			return Results.Ok(albumsByArtist.Select(t => new { t.Artist, t.Album }));
		} catch (Exception ex) {
			return Results.Problem("Error retrieving albums by artist: " + ex.Message);
		}
	}

	public static async Task<IResult> GetAlbum(string artist, string album, LibraryService libraryService)
	{
		try {
			var songs = await libraryService.GetAlbum(artist, album);
			return Results.Ok(songs.Select(s => new SongMetadata(s.Artist, s.Album, s.Title, s.TrackNumber, s.TrackTotal, null, s.Duration)));
		} catch (Exception ex) {
			return Results.Problem("Error retrieving songs by artist and album: " + ex.Message);
		}
	}

	public static async Task<IResult> SearchLibraryAsync(string query, LibraryService libraryService)
	{
		try {
			var results = await libraryService.SearchLibraryAsync(query);
			return Results.Ok(results.Select(s => new SongMetadata(s.Artist, s.Album, s.Title, s.TrackNumber, s.TrackTotal, null, s.Duration)));
		} catch (ArgumentException ex) {
			return Results.BadRequest(ex.Message);
		} catch (Exception ex) {
			return Results.Problem("Error searching library: " + ex.Message);
		}
	}

	public static async Task<IResult> RefreshAsync(LibraryService libraryService, ILogger<LibraryService> logger)
	{
		try {
			await libraryService.RefreshAsync();
			return Results.Ok(new { message = "Library scan completed successfully" });
		} catch (Exception ex) {
			logger.LogError(ex, "Error scanning library");
			return Results.Problem("Error scanning library: " + ex.Message);
		}
	}

	public static async Task<IResult> GetLibraryStatsAsync(LibraryService libraryService)
	{
		try {
			var stats = await libraryService.GetLibraryStatsAsync();
			return Results.Ok(stats);
		} catch (Exception ex) {
			return Results.Problem("Error retrieving library stats: " + ex.Message);
		}
	}
}
