using SongServer.Handlers;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace SongServer;
#pragma warning restore IDE0130 // Namespace does not match folder structure

public static class Extensions
{
	public static IEndpointRouteBuilder MapLibraryHandlers(this IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("library/");

		group.MapPost("/scan", LibraryHandlers.ScanLibraryAsync)
			.WithName("ScanLibrary")
			.WithSummary("Scan directories for music files");

		group.MapGet("/songs", LibraryHandlers.GetAllSongs)
			.WithName("GetAllSongs")
			.WithSummary("Get all songs in the library");

		group.MapGet("/artists", LibraryHandlers.GetAllArtists)
			.WithName("GetAllArtists")
			.WithSummary("Get all artists in the library");

		group.MapGet("/albums", LibraryHandlers.GetAllAlbums)
			.WithName("GetAllAlbums")
			.WithSummary("Get all albums in the library");

		group.MapGet("/search", LibraryHandlers.SearchLibrary)
			.WithName("SearchLibrary")
			.WithSummary("Search the library");

		group.MapGet("/artist/{artist}", LibraryHandlers.GetSongsByArtist)
			.WithName("GetSongsByArtist")
			.WithSummary("Get songs by artist");

		group.MapGet("/album/{album}", LibraryHandlers.GetSongsByAlbum)
			.WithName("GetSongsByAlbum")
			.WithSummary("Get songs by album");

		group.MapGet("/title/{title}", LibraryHandlers.GetSongsByTitle)
			.WithName("GetSongsByTitle")
			.WithSummary("Get songs by title");

		group.MapPost("/refresh", LibraryHandlers.RefreshLibraryAsync)
			.WithName("RefreshLibrary")
			.WithSummary("Refresh the library by rescanning existing directories");

		group.MapDelete("/cleanup", LibraryHandlers.CleanupLibraryAsync)
			.WithName("CleanupLibrary")
			.WithSummary("Remove deleted files from the library");

		group.MapGet("/stats", LibraryHandlers.GetLibraryStats)
			.WithName("GetLibraryStats")
			.WithSummary("Get library statistics");

		return builder;
	}

	public static IEndpointRouteBuilder MapPlayerHandlers(this IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("player/");

		group.MapGet("play", PlayerHandlers.Play);
		group.MapGet("pause", PlayerHandlers.Pause);
		group.MapGet("stop", PlayerHandlers.Stop);
		group.MapGet("advance", PlayerHandlers.Advance);
		group.MapGet("", PlayerHandlers.GetState);

		return builder;
	}
}
