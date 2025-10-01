using Microsoft.Extensions.Options;
using SongServer.Configuration;

namespace SongServer.Services;

internal class AudioPlayerService(AudioPlayer player, IOptions<PlayerOptions> options, ILogger<AudioPlayerService> logger) : IHostedService
{
	private readonly AudioPlayer _player = player;
	private readonly IOptions<PlayerOptions> _options = options;
	private readonly ILogger _logger = logger;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Loading playlist from {playlist}", _options.Value.Playlist);

		var playlist = await Playlist.Load(_options.Value.Playlist);
		// get the playlist state somehow
		var state = new AudioPlayer.PlaylistState() {
			Path = _options.Value.Playlist,
			Hash = playlist.Hash,
			ShuffleSeed = Random.Shared.Next(), // TODO: remove this
		};

		if (state.ShuffleSeed != null) {
			playlist.Shuffle(state.ShuffleSeed.Value);
		}

		_logger.LogInformation("Loading {count} entries from playlist.", playlist.Entries.Count);

		_player.SetPlaylist(playlist, state);
		//_ = _player.Play();
	}

	public Task StopAsync(CancellationToken cancellationToken) =>
		_player.Stop();
}
