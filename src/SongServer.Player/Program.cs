using SongServer.Configuration;
using SongServer.Handlers;
using SongServer.Services;
using SongServer.Services.Library;
using SongServer.Services.Mpd;

namespace SongServer;

internal class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection(PlayerOptions.SectionName));
		// builder.Services.AddTransient(sp => {
		// 	// I don't really know how else to do this... maybe it crashes?
		// 	var t = MpdClient.CreateClient(sp.GetRequiredService<ILogger<MpdClient>>());
		// 	return t.GetAwaiter().GetResult();
		// });
		builder.Services.AddTransient<MpdClient>();
		builder.Services.AddSingleton<AudioPlayer, BassAudioPlayer>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddSingleton<JsonLibrary>();
		builder.Services.AddDbContextFactory<LibraryContext>();

		// if (args.Length != 0) {
		// 	switch (args[0]) {
		// 		case "showoutput":
		// 			VlcAudioPlayer.DisplayOutputs();
		// 			return;
		// 	}
		// }

		builder.Services.AddHostedService<AudioPlayerService>();
		builder.Services.AddHostedService<PlayerHubBroadcaster>();

		builder.Services.AddCors();

		builder.Services.AddSignalR();

		var app = builder.Build();

		PlayerHandlers.Map(app);
		LibraryHandlers.Map(app);
		PlaylistHandlers.Map(app);

		app.MapHub<PlayerHub>("/player/hub");

		app.UseCors(builder => builder
			.AllowAnyOrigin()
			.AllowAnyHeader()
			.AllowAnyMethod());

		// using (var scope = app.Services.CreateScope()) {
		// 	var library = scope.ServiceProvider.GetRequiredService<LibraryService>();

		// 	await library.InitializeAsync([
		// 		@"\\vmhost.local\media\Audio\Soundtracks",
		// 		@"\\vmhost.local\media\Audio\Singles",
		// 		@"\\vmhost.local\media\Audio\Full CDs",
		// 	]);
		// 	return;
		// }

		// using (var scope = app.Services.CreateScope()) {
		// 	using var context = scope.ServiceProvider.GetRequiredService<LibraryContext>();

		// 	var playlist = Playlist.Load(context.Playlist.Where(s => s.Song != null).Select(s => s.Song!.Url));
		// 	var state    = new AudioPlayer.PlaylistState() {
		// 		Path = "nowhere_in_particular",
		// 		Hash = playlist.Hash,
		// 		ShuffleSeed = Random.Shared.Next(),
		// 	};

		// 	if (state.ShuffleSeed != null) {
		// 		playlist.Shuffle(state.ShuffleSeed.Value);
		// 	}

		// 	var player = scope.ServiceProvider.GetRequiredService<AudioPlayer>();

		// 	Console.WriteLine("press a key to set the playlist...");
		// 	Console.ReadKey(true);
		// 	player.SetPlaylist(playlist, state);

		// 	Console.WriteLine("press a key to play...");
		// 	Console.ReadKey(true);
		// 	player.Play();

		// 	Console.WriteLine("press a key to stop...");
		// 	Console.ReadKey(true);
		// 	player.Stop();

		// 	return;
		// }

		// using (var scope = app.Services.CreateScope()) {
		// 	var player = scope.ServiceProvider.GetRequiredService<AudioPlayer>();
		// 	using var context = scope.ServiceProvider.GetRequiredService<LibraryContext>();

		// 	var playlist = Playlist.Load(context.Playlist.Where(s => s.Song != null).Select(s => s.Song!.Url));
		// 	var state    = new AudioPlayer.PlaylistState() {
		// 		Path = "nowhere_in_particular",
		// 		Hash = playlist.Hash,
		// 		ShuffleSeed = Random.Shared.Next(),
		// 	};

		// 	if (state.ShuffleSeed != null) {
		// 		playlist.Shuffle(state.ShuffleSeed.Value);
		// 	}

		// 	player.SetPlaylist(playlist, state);

		// 	await player.Play();
		// 	await Task.Delay(TimeSpan.FromSeconds(10));
		// 	await player.Stop();

		// 	return;
		// }


// 		using (var scope = app.Services.CreateScope()) {
// 			var library = scope.ServiceProvider.GetRequiredService<LibraryContext>();
// 			var jl = scope.ServiceProvider.GetRequiredService<JsonLibrary>();

// 			var songs = library.Songs
// 				.Include(s => s.Source)
// 				.Select(s => new JsonLibrary.JsonSong() {
// 					SourcePath   = s.Source!.Path,
// 					RelativePath = s.RelativePath,
// 					Artist       = s.Artist,
// 					Album        = s.Album,
// 					Title        = s.Title,
// 					TrackNumber  = s.TrackNumber,
// 					TrackTotal   = s.TrackTotal,
// 					Duration     = s.Duration,
// 					FileModified = s.FileModified,
// 					FileHash     = s.FileHash,
// 					FileSize     = s.FileSize,
// 				});
// var start = Stopwatch.GetTimestamp();
// 			await jl.Store(songs);
// Console.WriteLine($"Stored in {Stopwatch.GetElapsedTime(start)}");
// start = Stopwatch.GetTimestamp();
// 			await jl.Store(songs);
// Console.WriteLine($"Stored in {Stopwatch.GetElapsedTime(start)}");
// 			return;
// 		}

		await app.RunAsync();
	}

	private static async Task DoSomeThings()
	{
		await Task.CompletedTask;

		//AudioPlayer.DisplayOutputs();

		// using var player = new AudioPlayer(pl, new AudioPlayer.PlaylistState() {
		// 	Path = "my imagination",
		// 	Hash = pl.Hash,
		// });

		// Console.WriteLine("Starting playback (10s)...");
		// player.Play();
		// await Task.Delay(TimeSpan.FromSeconds(10));

		// Console.WriteLine("Stopping playback (5s)...");		
		// player.Stop();
		// await Task.Delay(TimeSpan.FromSeconds(5));

		// Console.WriteLine("Starting playback (10s)...");
		// player.Play();
		// await Task.Delay(TimeSpan.FromSeconds(10));

		// Console.WriteLine("Pausing playback (5s)...");		
		// player.Pause();
		// await Task.Delay(TimeSpan.FromSeconds(5));

		// Console.WriteLine("Resuming playback (5s)...");		
		// player.Play();
		// await Task.Delay(TimeSpan.FromSeconds(30));

		// play through the queue for 5 minutes (so the player will advance through the queue)
		// player.Play();
		// await Task.Delay(TimeSpan.FromMinutes(5));

		// await player.Play(new Uri("file://vmhost.local/media/Audio/Full%20CDs/311/311/01%20-%20Down.mp3"));
		// await Task.Delay(5000);
		// await player.Play(new Uri("file://vmhost.local/media/Audio/Full%20CDs/Blink%20182/Enema%20Of%20The%20State/08%20-%20All%20the%20Small%20Things.mp3"));
		// await Task.Delay(5000);
		// await player.Stop();
	}
}
