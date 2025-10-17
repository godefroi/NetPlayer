using Microsoft.AspNetCore.SignalR;

namespace SongServer.Services;

internal class PlayerHubBroadcaster(AudioPlayer player, IHubContext<PlayerHub, IPlayerHub> hub) : IHostedService
{
	private readonly AudioPlayer _player = player;
	private readonly IHubContext<PlayerHub, IPlayerHub> _hub = hub;
	private SongMetadata? _song;

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_player.StateChanged += OnStateChanged;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_player.StateChanged -= OnStateChanged;
		return Task.CompletedTask;
	}

	private void OnStateChanged(object? sender, StateChangedEventArgs e)
	{
		_hub.Clients.All.UpdatePosition(e.State.GetValueOrDefault(State.Stopped), e.Position);

		if (e.Song != _song) {
			_hub.Clients.All.UpdateMedia(e.Song);
			_song = e.Song;
		}
	}
}
