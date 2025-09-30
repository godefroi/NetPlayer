using Microsoft.AspNetCore.Mvc;
using SongServer.Services.Library;

namespace SongServer.Handlers;

internal class LibraryHandlers
{
    public static async Task<IResult> ScanLibraryAsync(
        [FromBody] ScanRequest request,
        LibraryService libraryService,
        ILogger<LibraryService> logger)
    {
        try
        {
            await libraryService.InitializeAsync(request.Paths);
            return Results.Ok(new { message = "Library scan completed successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error scanning library");
            return Results.Problem("Error scanning library: " + ex.Message);
        }
    }

    public static IResult GetAllSongs(LibraryService libraryService)
    {
        var songs = libraryService.GetAllSongs();
        return Results.Ok(songs);
    }

    public static IResult GetAllArtists(LibraryService libraryService)
    {
        var artists = libraryService.GetAllArtists();
        return Results.Ok(artists);
    }

    public static IResult GetAllAlbums(LibraryService libraryService)
    {
        var albums = libraryService.GetAllAlbums();
        return Results.Ok(albums);
    }

    public static IResult SearchLibrary(
        string query,
        LibraryService libraryService)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest("Query parameter is required");
        }

        var results = libraryService.Search(query);
        return Results.Ok(results);
    }

    public static IResult GetSongsByArtist(
        string artist,
        bool exactMatch,
        LibraryService libraryService)
    {
        var songs = libraryService.FindByArtist(artist, exactMatch);
        return Results.Ok(songs);
    }

    public static IResult GetSongsByAlbum(
        string album,
        bool exactMatch,
        LibraryService libraryService)
    {
        var songs = libraryService.FindByAlbum(album, exactMatch);
        return Results.Ok(songs);
    }

    public static IResult GetSongsByTitle(
        string title,
        bool exactMatch,
        LibraryService libraryService)
    {
        var songs = libraryService.FindByTitle(title, exactMatch);
        return Results.Ok(songs);
    }

    public static async Task<IResult> RefreshLibraryAsync(LibraryService libraryService)
    {
        try
        {
            await libraryService.RefreshAsync();
            return Results.Ok(new { message = "Library refresh completed successfully" });
        }
        catch (Exception ex)
        {
            return Results.Problem("Error refreshing library: " + ex.Message);
        }
    }

    public static async Task<IResult> CleanupLibraryAsync(LibraryService libraryService)
    {
        try
        {
            await libraryService.RemoveDeletedFilesAsync();
            return Results.Ok(new { message = "Library cleanup completed successfully" });
        }
        catch (Exception ex)
        {
            return Results.Problem("Error cleaning up library: " + ex.Message);
        }
    }

    public static IResult GetLibraryStats(LibraryService libraryService)
    {
        var stats = new
        {
            TotalSongs = libraryService.TotalSongs,
            TotalArtists = libraryService.GetAllArtists().Count(),
            TotalAlbums = libraryService.GetAllAlbums().Count()
        };
        return Results.Ok(stats);
    }
}

public record ScanRequest(string[] Paths);
