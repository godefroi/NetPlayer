using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SongServer.Configuration;
using SongServer.Data;

namespace SongServer.Services;

internal class AudioPlayerService(AudioPlayer player, Playlist playlist, IDbContextFactory<LibraryContext> contextFactory, IOptions<PlayerOptions> options, ILogger<AudioPlayerService> logger) : IHostedService
{
	private readonly AudioPlayer _player = player;
	private readonly Playlist _playlist = playlist;
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

		var state = await context.State.GetStateAsync<PlaylistState>("playlist_state", cancellationToken);

		if (state == null || state.Hash != _playlist.Hash) {
			state = new(
				_playlist.Hash,
				Random.Shared.Next()
			);
		}

		_logger.LogInformation("Loaded {count} entries from playlist.", _playlist.Count);

		if (state.ShuffleSeed != null) {
			_playlist.Shuffle(state.ShuffleSeed.Value, state.CurrentIndex);
			_logger.LogInformation("Shuffled playlist with seed {seed}.", state.ShuffleSeed);
		}

		_player.SetPlaylist(_playlist);
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

		var state = new PlaylistState(_playlist.Hash, _playlist.ShuffleSeed, _playlist.CurrentIndex);

		await context.State.SetState("playlist_state", state, cancellationToken);
		await context.SaveChangesAsync(cancellationToken);
	}

	internal record PlaylistState(string Hash, int? ShuffleSeed = null, int? CurrentIndex = null)
	{
		public static PlaylistState Empty { get; } = new(string.Empty);
	}
}
