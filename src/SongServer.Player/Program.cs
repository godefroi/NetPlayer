using System.IO.Pipelines;
using FFmpeg.AutoGen;
using SDL3;
using SongServer.Audio.Ffmpeg;
using SongServer.Configuration;
using SongServer.Handlers;
using SongServer.Services;
using SongServer.Services.Library;
using SongServer.Services.Smb;

namespace SongServer;

internal class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection(PlayerOptions.SectionName));

		builder.Services.AddSingleton<SmbFileClient>();
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
		var logger = app.Services.GetRequiredService<ILogger<Program>>();

		PlayerHandlers.Map(app);
		LibraryHandlers.Map(app);
		PlaylistHandlers.Map(app);

		app.MapHub<PlayerHub>("/player/hub");

		app.UseCors(builder => builder
			.AllowAnyOrigin()
			.AllowAnyHeader()
			.AllowAnyMethod());


		InitializeSdl();

		using var eventCts = new CancellationTokenSource();
		var eventTask = HandleSdlEvents(logger, eventCts.Token);

		//await app.RunAsync();
		using (var scope = app.Services.CreateScope()) {
			//await TestSdlAudio(scope, logger);
			await TestFfmpegAudio(scope, logger);
		}

		eventCts.Cancel();
		await eventTask;

		ShutdownSdl(logger);
	}

	private static async Task TestSdlAudio(IServiceScope scope, ILogger logger)
	{
		using var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

		var inputSpec = new SDL.AudioSpec() {
			Freq = 44100,
			Channels = 2,
			Format = SDL.AudioFormat.AudioS16LE,
		};

		using var d = new Audio.Sdl.SdlAudioDevice(gain: 0.2f);
		using var s = new Audio.Sdl.SdlAudioStream(inputSpec, d, scope.ServiceProvider.GetRequiredService<ILogger<Audio.Sdl.SdlAudioStream>>());
		using var fs = File.OpenRead("output.pcm");

		s.Progress += (_, e) => {
			logger.LogInformation("Stream position: [{pos}]", e.Position);
		};

		await s.WritePcmAudio(fs, cts.Token);
	}

	private static async Task TestFfmpegAudio(IServiceScope scope, ILogger logger)
	{
		await Task.CompletedTask;

		// https://blog.gaein.cn/passages/beginner-guide-to-ffmpeg-autogen/
		ffmpeg.RootPath = @"C:\Users\MarkParker\source\repos\song-server\src\SongServer.Player\_ffmpeg_bin";

		using var fsi = File.OpenRead(@"\\vmhost.local\media\Audio\Full CDs\311\311 (30th Anniversary Edition)\01 Down.opus");
		using var fso = File.Create("pcm_output_frompipe.pcm");

		AudioConverter.ConvertToPcm(@"\\vmhost.local\media\Audio\Full CDs\311\311 (30th Anniversary Edition)\01 Down.opus", fsi, PipeReader.Create(fsi), PipeWriter.Create(fso));
	}

	private static void InitializeSdl()
	{
		// we'll handle our own ctrl-c, thank you very much
		SDL.SetHint(SDL.Hints.NoSignalHandlers, "1");

		// we need to initialize the SDL audio subsystem (which also initializes the events subsystem)
		if (!SDL.InitSubSystem(SDL.InitFlags.Audio)) {
			var err = SDL.GetError();
			throw new Exception($"Unable to initialize SDL audio subsystem: {err}");
		}
	}

	private static void ShutdownSdl(ILogger logger)
	{
		logger.LogInformation("Shutting down SDL...");
		// shut down the audio subsystem (and events too)
		SDL.QuitSubSystem(SDL.InitFlags.Audio);
		SDL.QuitSubSystem(SDL.InitFlags.Events);
		SDL.Quit();
		logger.LogInformation("SDL shutdown complete.");
	}

	private static async Task HandleSdlEvents(ILogger logger, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested) {
			try {
				while (SDL.PollEvent(out var ev)) {
					// We ain't got no events... we don't need no events. I don't have to show you any stinking events!
					logger.LogInformation("SDL event of type {event}", (SDL.EventType)ev.Type);
					// ... but I will...
				}

				await Task.Delay(100, cancellationToken);
			} catch (TaskCanceledException) {
				// if we were cancelled, we should leave
				return;
			}
		}
	}
}
