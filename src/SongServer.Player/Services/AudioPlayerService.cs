using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SongServer.Configuration;
using SongServer.Data;

namespace SongServer.Services;

internal class AudioPlayerService(AudioPlayer player, IDbContextFactory<LibraryContext> contextFactory, IOptions<PlayerOptions> options, ILogger<AudioPlayerService> logger) : IHostedService
{
	private readonly AudioPlayer _player = player;
	private readonly IOptions<PlayerOptions> _options = options;
	private readonly IDbContextFactory<LibraryContext> _contextFactory = contextFactory;
	private readonly ILogger _logger = logger;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

		var playlistSongs = context.Playlist
			.AsNoTracking()
			.AsEnumerable()
			.Select(s => new SongReference(s.SourceId, s.RelativePath));

		var playlist = new Playlist(playlistSongs);

		var state = await context.State.GetState<AudioPlayer.PlaylistState>("playlist_state", cancellationToken);

		if (state == null || state.Hash != playlist.Hash) {
			state = new AudioPlayer.PlaylistState() {
				Hash        = playlist.Hash,
				ShuffleSeed = Random.Shared.Next(),
			};
		}

		_logger.LogInformation("Loaded {count} entries from playlist.", playlist.Entries.Count);

		if (state.ShuffleSeed != null) {
			playlist.Shuffle(state.ShuffleSeed.Value);
			_logger.LogInformation("Shuffled playlist with seed {seed}.", state.ShuffleSeed);
		}

		_player.SetPlaylist(playlist, state);
		_player.PlaylistStateChanged += (_, _) => { _ = SavePlaylistState(); };
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _player.Stop();

		await SavePlaylistState(cancellationToken);
	}

	private async Task SavePlaylistState(CancellationToken cancellationToken = default)
	{
		using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

		await context.State.SetState("playlist_state", _player.GetPlaylistState(), cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
	}
}
