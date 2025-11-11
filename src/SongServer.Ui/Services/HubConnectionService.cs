using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace SongServer.Services;

public class HubConnectionService : IAsyncDisposable
{
	private HubConnection _hubConnection;

	public HubConnection? HubConnection => _hubConnection;

	public HubConnectionService(IOptions<PlayerClientOptions> options)
	{
 		_hubConnection = new HubConnectionBuilder()
			.WithUrl(new Uri(options.Value.ServerUri, "/player/hub"))
			.WithStatefulReconnect()
			.WithAutomaticReconnect([TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
			.Build();
	}

	public Task InitializeAsync()
	{
		if (_hubConnection.State != HubConnectionState.Disconnected) {
			return Task.CompletedTask;
		}

		return _hubConnection.StartAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_hubConnection is not null) {
			await _hubConnection.DisposeAsync();
		}

		GC.SuppressFinalize(this);
	}
}
