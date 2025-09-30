using Microsoft.AspNetCore.Mvc;
using SongServer.Services;

namespace SongServer.Handlers;

internal class PlayerHandlers
{
	private PlayerHandlers() {}

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
}
