using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SongServer.Services;
using SongServer.Services.Library;

namespace SongServer.Handlers;

internal class PlayerHandlers
{
	private PlayerHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("player/");

		group.MapGet("play", PlayerHandlers.Play);
		group.MapGet("pause", PlayerHandlers.Pause);
		group.MapGet("stop", PlayerHandlers.Stop);
		group.MapGet("advance", PlayerHandlers.Advance);
		group.MapGet("enqueue", PlayerHandlers.Enqueue);
		group.MapGet("", PlayerHandlers.GetState);
	}

	public static async Task<PlayerState> Play([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		await player.Play();

		return player.GetState();
	}

	public static Task Pause([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		player.Pause();

		return Task.CompletedTask;
	}

	public static Task Stop([FromServices]AudioPlayer player, CancellationToken cancellationToken = default) =>
		player.Stop();

	public static async Task<PlayerState> Advance([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		await player.Advance();

		return player.GetState();
	}

	public static Task<PlayerState> GetState(AudioPlayer player, CancellationToken cancellationToken = default) =>
		Task.FromResult(player.GetState());

	public static async Task<Results<ProblemHttpResult, Ok<PlayerState>>> Enqueue([FromServices]AudioPlayer player, [FromServices]LibraryService library, string? artist, string? album, string? title, CancellationToken cancellationToken = default)
	{
		// find the song in the library by whatever criteria we were provided
		var songs = await library.FindSongsAsync(artist, album, title);

		// if multiple songs were found, return an error
		if (songs.Count == 0) {
			return TypedResults.Problem(new() {
				Status = 422,
				Title = "No songs found matching the specified criteria.",
			});
		}

		if (songs.Count > 1) {
			return TypedResults.Problem(new() {
				Status = 422,
				Title = "Multiple songs found matching the criteria. Please be more specific.",
				Extensions = {
					{ "songCount", songs.Count }
				}
			});
		}

		player.Enqueue(songs[0].Url);

		return TypedResults.Ok(player.GetState());
	}
}
