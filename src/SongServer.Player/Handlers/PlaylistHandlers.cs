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
			var songs = await context.Playlist
				.Select(p => new Song(p.Song!))
				.ToListAsync();

			return Results.Ok(songs);
		} catch (Exception ex) {
			return Results.Problem("Error retrieving playlist: " + ex.Message);
		}
	}

	public static async Task<IResult> AddToPlaylist([FromBody]Song request, LibraryContext context)
	{
		if (request == null) {
			return Results.BadRequest("Request body is required");
		}

		try {
			var (song, error) = await FindSongAsync(request, context);

			if (error != null) {
				return error;
			}

			var existing = await context.Playlist.FindAsync(song!.SourceId, song.RelativePath);

			if (existing != null) {
				return Results.Conflict(new { message = "Playlist entry already exists" });
			}

			context.Playlist.Add(new PlaylistFile { SourceId = song.SourceId, RelativePath = song.RelativePath });
			await context.SaveChangesAsync();

			return Results.Ok(new { message = "Added to playlist" });
		} catch (Exception ex) {
			return Results.Problem("Error adding to playlist: " + ex.Message);
		}
	}

	public static async Task<IResult> RemoveFromPlaylist([FromQuery]string artist, [FromQuery]string title, LibraryContext context)
	{
		try {
			var request = new Song(artist, string.Empty, title, null, null, TimeSpan.Zero);
			var (song, error) = await FindSongAsync(request, context);

			if (error != null) {
				return error;
			}

			var entry = await context.Playlist.FindAsync(song!.SourceId, song.RelativePath);

			if (entry == null) {
				return Results.NotFound(new { message = "Playlist entry not found for the specified song" });
			}

			context.Playlist.Remove(entry);
			await context.SaveChangesAsync();

			return Results.Ok(new { message = "Removed from playlist" });
		} catch (Exception ex) {
			return Results.Problem("Error removing from playlist: " + ex.Message);
		}
	}

	private static async Task<(Services.Library.Song? Song, IResult? Error)> FindSongAsync(Song request, LibraryContext context)
	{
		var query = context.Songs.AsQueryable();

		if (!string.IsNullOrWhiteSpace(request.Artist)) {
			query = query.Where(s => s.Artist == request.Artist);
		}

		if (!string.IsNullOrWhiteSpace(request.Album)) {
			query = query.Where(s => s.Album == request.Album);
		}

		if (!string.IsNullOrWhiteSpace(request.Title)) {
			query = query.Where(s => s.Title == request.Title);
		}

		var matches = await query.ToListAsync();

		if (matches.Count == 0) {
			return (null, TypedResults.Problem(new() { Status = 422, Title = "No songs found matching the specified criteria." }));
		}

		// TODO: we might need to set up a source priority, because a song might exist in multiple sources
		if (matches.Count > 1) {
			return (null, TypedResults.Problem(new() { Status = 422, Title = "Multiple songs found matching the criteria. Please be more specific.", Extensions = { { "songCount", matches.Count } } }));
		}

		return (matches[0], null);
	}

	internal record SongRequest(string? Artist, string? Album, string? Title);
}
