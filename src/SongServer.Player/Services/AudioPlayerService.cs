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

		// here's where we'd load our playlist state if we had previously saved
		// it... we'd verify the hash to ensure it was for the right playlist
		// otherwise, we can just make a new one for the new playlist
		var state = new AudioPlayer.PlaylistState() {
			Hash        = playlist.Hash,
			ShuffleSeed = Random.Shared.Next(),
		};

		_logger.LogInformation("Loaded {count} entries from playlist.", playlist.Entries.Count);

		if (state.ShuffleSeed != null) {
			playlist.Shuffle(state.ShuffleSeed.Value);
			_logger.LogInformation("Shuffled playlist with seed {seed}.", state.ShuffleSeed);
		}

		_player.SetPlaylist(playlist, state);
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _player.Stop();

		// here is where we'd save the playlist state for
		// our next startup
	}
}
