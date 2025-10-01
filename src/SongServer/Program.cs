using SongServer.Configuration;
using SongServer.Handlers;
using SongServer.Services;
using SongServer.Services.Library;

namespace SongServer;

internal class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection(PlayerOptions.SectionName));
		builder.Services.AddSingleton<AudioPlayer>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddDbContextFactory<LibraryContext>();

		builder.Services.AddHostedService<AudioPlayerService>();

		var app = builder.Build();

		PlayerHandlers.Map(app);
		LibraryHandlers.Map(app);

		// using (var scope = app.Services.CreateScope()) {
		// 	var library = scope.ServiceProvider.GetRequiredService<LibraryService>();

		// 	await library.InitializeAsync([
		// 		@"\\vmhost.local\media\Audio\Soundtracks",
		// 		@"\\vmhost.local\media\Audio\Singles",
		// 		@"\\vmhost.local\media\Audio\Full CDs",
		// 	]);
		// }

		await app.RunAsync();
	}

	private static async Task DoSomeThings()
	{
		await Task.CompletedTask;

		//AudioPlayer.DisplayOutputs();

		var pl = new Playlist([
			@"\\vmhost.local\media\Audio\Full CDs\Blink 182\Enema Of The State\08 - All the Small Things.mp3",
			@"\\vmhost.local\media\Audio\Full CDs\Blind Melon\Blind Melon\07 - No Rain.mp3",
			@"\\vmhost.local\media\Audio\Full CDs\Blur\Blur\02 - Song 2.mp3"
		]);

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
