using Microsoft.AspNetCore.SignalR;

namespace SongServer.Services;

internal class PlayerHub(AudioPlayer player) : Hub<IPlayerHub>
{
	private readonly AudioPlayer _player = player;

	public override async Task OnConnectedAsync()
	{
		// send out the info from the player
		var client = Clients.Client(Context.ConnectionId);

		var (state, song, pos) = _player.GetState();

		await client.UpdateMedia(song);
		await client.UpdatePosition(state, pos);

		await base.OnConnectedAsync();
	}
}

public interface IPlayerHub
{
	Task UpdatePosition(State state, TimeSpan? position);

	Task UpdateMedia(SongMetadata? song);
}
