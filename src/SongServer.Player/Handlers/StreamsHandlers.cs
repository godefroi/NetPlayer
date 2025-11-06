using Microsoft.EntityFrameworkCore;
using SongServer.Data;

namespace SongServer.Handlers;

internal class StreamsHandlers
{
	private StreamsHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("streams/");

		group.MapGet("popularAlbums", GetPopularAlbums)
			.WithName("GetPopularAlbums")
			.WithSummary("Get albums most commonly streamed from");
	}

	public static async Task<IResult> GetPopularAlbums(LibraryContext context)
	{
		try {
			var results = await context.StreamMetadata
				.Select(s => new { s.Artist, s.Album, s.Title })
				.Distinct()
				.GroupBy(x => new { x.Artist, x.Album })
				.Where(g => g.Count() > 1)
				.Select(g => new
				{
					g.Key.Artist,
					g.Key.Album,
					Count = g.Count()
				})
				.OrderByDescending(x => x.Count)
				.ToListAsync();

			return Results.Ok(results);
		} catch (Exception ex) {
			return Results.Problem("Error retrieving popular albums: " + ex.Message);
		}
	}
}
