using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SongServer.Configuration;
using SongServer.Services.Library;

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

		var playlist = new Playlist(context.Playlist.AsNoTracking().AsEnumerable().Select(s => new SongReference(s.SourceId, s.RelativePath)));
		// var playlist = new Playlist([
		// 	new SongReference(3, @"311\Evolver\01 - Creatures (For A While).mp3"),
		// 	new SongReference(3, @"Space\Spiders\01 Neighbourhood.m4a"),
		// 	new SongReference(3, @"Metal X-Mas 2CD Special Edition\101 - Jeff Scott Soto, Bruce Kulick, Bob Kulick, Chris Wyse, Ray Luzier - We Wish You A Merry Xmas.flac"),
		// 	new SongReference(3, @"311\311 (30th Anniversary Edition)\01 Down.opus"),
		// ]);
		var state    = new AudioPlayer.PlaylistState() {
			Path = "nowhere_in_particular",
			Hash = playlist.Hash,
			ShuffleSeed = 12345,//Random.Shared.Next(),
		};

		if (state.ShuffleSeed != null) {
			playlist.Shuffle(state.ShuffleSeed.Value);
		}

		_logger.LogInformation("Loading {count} entries from playlist.", playlist.Entries.Count);

		_player.SetPlaylist(playlist, state);
	}

	public Task StopAsync(CancellationToken cancellationToken) => _player.Stop();
}
