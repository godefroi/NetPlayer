using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SongServer.Services.Library;

namespace SongServer.Handlers;

internal class PlaylistHandlers
{
	private PlaylistHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("playlist/");

		group.MapGet("", GetPlaylist)
			.WithName("GetPlaylist")
			.WithSummary("Get the current playlist entries");

		group.MapPost("", AddToPlaylist)
			.WithName("AddToPlaylist")
			.WithSummary("Add a song to the playlist by song metadata (artist/album/title)");

		group.MapDelete("", RemoveFromPlaylist)
			.WithName("RemoveFromPlaylist")
			.WithSummary("Remove a song from the playlist by song metadata (artist/album/title)");
	}

	public static async Task<IResult> GetPlaylist(LibraryContext context)
	{
		try {
			var songs = (await context.Playlist
				.AsNoTracking()
				.Include(p => p.Song)
				.ToListAsync())
				.Select(p => new SongMetadata(p.Song!.AlbumArtists, p.Song!.Album, p.Song!.Title, p.Song!.TrackNumber, p.Song!.TrackCount, null, p.Song!.Duration))
				.ToList();

			return Results.Ok(songs);
		} catch (Exception ex) {
			return Results.Problem("Error retrieving playlist: " + ex.Message);
		}
	}

	public static async Task<IResult> AddToPlaylist([FromBody]SongMetadata request, LibraryContext context, ILogger<PlaylistHandlers> logger)
	{
		if (request == null) {
			return Results.BadRequest("Request body is required");
		}

		try {
			var (song, error) = await FindSongAsync(request, context, logger);

			if (error != null) {
				return TypedResults.Problem(error);
			}

			var existing = await context.Playlist.FindAsync(song!.SourceId, song.RelativePath);

			if (existing != null) {
				return Results.Conflict(new { message = "Playlist entry already exists" });
			}

			var newFile = context.Playlist.Add(new PlaylistFile { SourceId = song.SourceId, RelativePath = song.RelativePath });
			await context.SaveChangesAsync();

			logger.LogInformation("Added file {relativePath} to playlist", newFile.Entity.RelativePath);

			return Results.Ok(new { message = "Added to playlist" });
		} catch (Exception ex) {
			return Results.Problem("Error adding to playlist: " + ex.Message);
		}
	}

	public static async Task<Results<Ok<OperationResult>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>, ProblemHttpResult>> RemoveFromPlaylist([FromQuery]string artist, [FromQuery]string title, LibraryContext context)
	{
		try {
			var request = new SongMetadata([artist], string.Empty, title, null, null, null, TimeSpan.Zero);
			var (song, error) = await FindSongAsync(request, context);

			if (error != null) {
				return TypedResults.BadRequest(error);
			}

			var entry = await context.Playlist.FindAsync(song!.SourceId, song.RelativePath);

			if (entry == null) {
				return TypedResults.NotFound<ProblemDetails>(new() {  Title = "Playlist entry not found for the specified song" });
			}

			context.Playlist.Remove(entry);
			await context.SaveChangesAsync();

			return TypedResults.Ok(new OperationResult("Removed from playlist"));
		} catch (Exception ex) {
			return TypedResults.Problem("Error removing from playlist: " + ex.Message);
		}
	}

	private static async Task<(Song? Song, ProblemDetails? Error)> FindSongAsync(SongMetadata request, LibraryContext context, ILogger? logger = null)
	{
		try {
			var query = context.Songs.AsQueryable();

			// TODO: this isn't quite right... we need to decide whether we're doing artists or performers, and whether they're AND or OR
			if (request.Artists != null && request.Artists.Count > 0 && !string.IsNullOrWhiteSpace(request.Artists[0])) {
				query = query.Where(s => s.AlbumArtists != null && s.AlbumArtists.Any(aa => request.Artists.Contains(aa)));
			}

			if (!string.IsNullOrWhiteSpace(request.Album)) {
				query = query.Where(s => s.Album == request.Album);
			}

			if (!string.IsNullOrWhiteSpace(request.Title)) {
				query = query.Where(s => s.Title == request.Title);
			}

			var matches = await query.ToListAsync();

			if (matches.Count == 0) {
				return (null, new ProblemDetails() { Status = 422, Title = "No songs found matching the specified criteria." });
			}

			// TODO: we might need to set up a source priority, because a song might exist in multiple sources
			if (matches.Count > 1) {
				return (null, new ProblemDetails() { Status = 422, Title = "Multiple songs found matching the criteria. Please be more specific.", Extensions = { { "songCount", matches.Count } } });
			}

			return (matches[0], null);
		} catch (Exception e) {
			logger?.LogError(e, "An error occurred while searching the library");
			return (null, new ProblemDetails() { Status = 500, Title = e.Message });
		}
	}

	internal record SongRequest(string? Artist, string? Album, string? Title);
}
