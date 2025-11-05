using System.IO.Pipelines;
using FFmpeg.AutoGen;
using SDL3;
using SongServer.Audio.FFmpeg;
using SongServer.Configuration;
using Microsoft.EntityFrameworkCore;
using SongServer.Data;
using SongServer.Handlers;
using SongServer.Services;
using SongServer.Services.Smb;
using SongServer.Audio.Sdl;
using SongServer.Audio;

namespace SongServer;

internal class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection(PlayerOptions.SectionName));
		builder.Services.Configure<StreamMetadataOptions>(builder.Configuration.GetSection(StreamMetadataOptions.SectionName));

		builder.Services.AddSingleton<SmbFileClient>();
		//builder.Services.AddSingleton<AudioPlayer, BassAudioPlayer>();
		builder.Services.AddSingleton<AudioPlayer, FFmpegAudioPlayer>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddDbContextFactory<LibraryContext>();

		if (args.Length != 0) {
			switch (args[0]) {
				case "showoutputs":
					BassAudioPlayer.DisplayOutputs();
					return;
			}
		}

		builder.Services.AddHostedService<AudioPlayerService>();
		builder.Services.AddHostedService<PlayerHubBroadcaster>();
		if (builder.Configuration.GetSection(StreamMetadataOptions.SectionName).Exists()) {
			builder.Services.AddHostedService<StreamMetadataService>();
		}

		builder.Services.AddCors();

		builder.Services.AddSignalR();

		var app = builder.Build();

		await ExecutePendingMigrations(app.Services);

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

		FFmpegSystem.Initialize(app.Services.GetRequiredService<ILogger<FFmpegSystem>>());

		using var eventCts = new CancellationTokenSource();
		var eventTask = HandleSdlEvents(logger, eventCts.Token);

		await app.RunAsync();
		// using (var scope = app.Services.CreateScope()) {
		// 	//await TestSdlAudio(scope, logger);
		// 	await TestFfmpegAudio(scope, logger);
		// 	//await CheckAllAudioFormats(scope, logger);
		// }

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

	private static async Task ExecutePendingMigrations(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
	{
		using var scope = serviceProvider.CreateScope();

		var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
		var context = scope.ServiceProvider.GetRequiredService<LibraryContext>();

		var migrations = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

		if (migrations.Count == 0) {
			logger.LogInformation("No pending migrations.");
			return;
		}

		foreach (var migration in migrations) {
			logger.LogInformation("Pending migration: {migration}", migration);
		}

		await context.Database.MigrateAsync(cancellationToken);
	}

	private static async Task TestFfmpegAudio(IServiceScope scope, ILogger logger)
	{
		await Task.CompletedTask;

		#pragma warning disable CS0219
		var fn_1ch = @"\\vmhost.local\media\Audio\Singles\OPM\OPM - Heaven is a Halfpipe.mp3";
		var fn_fple48k = @"\\vmhost.local\media\Audio\Soundtracks\Descent II\02-Title.opus";
		var fn_fple44k = @"\\vmhost.local\media\Audio\Soundtracks\Romeo and Juliet\01 - Garbage - #1 Crush.mp3";
		var fn_s16le = @"\\vmhost.local\media\Audio\Full CDs\Metal X-Mas 2CD Special Edition\101 - Jeff Scott Soto, Bruce Kulick, Bob Kulick, Chris Wyse, Ray Luzier - We Wish You A Merry Xmas.flac";
		var fn_fple22k = @"\\vmhost.local\media\Audio\Full CDs\Incubus\Make Yourself\08 - Drive.mp3";
		#pragma warning restore CS0219

		using var fsi = File.OpenRead(fn_fple22k);
		using var conv = new AudioConverter(fsi, scope.ServiceProvider.GetRequiredService<ILogger<AudioConverter>>());

		//Console.WriteLine($"stream #{conv.StreamIndex}");
		//Console.WriteLine($"Input spec: {conv.InputSpec}");

		using var sad = new SdlAudioDevice();
		var devSpec = SdlAudioDevice.GetDeviceAudioSpec();

		//Console.WriteLine($"device native spec: format = {devSpec.Format}, channels = {devSpec.Channels}, frequency = {devSpec.Freq}");

		using var sao = new SdlAudioOutputStream(devSpec, sad, scope.ServiceProvider.GetRequiredService<ILogger<SdlAudioOutputStream>>());

		conv.ConvertStream(sao, MakeResamplerSpec(devSpec));

		Console.WriteLine("Writing finished, waiting for remaining playtime...");
		await sao.WaitForFinishAsync();

		static PcmAudioSpec MakeResamplerSpec(SDL.AudioSpec sdlSpec) => new(
			sdlSpec.Format.AsFormat(),
			sdlSpec.Channels,
			sdlSpec.Freq
		);
	}

	private static async Task CheckAllAudioFormats(IServiceScope scope, ILogger logger)
	{
		await Task.CompletedTask;

		var formats = new Dictionary<string, int>();
		
		using var context = scope.ServiceProvider.GetRequiredService<LibraryContext>();

		foreach (var s in context.Songs) {
			using var fsi = File.OpenRead(Path.Join(s.SourcePath, s.RelativePath));
			using var conv = new AudioConverter(fsi, scope.ServiceProvider.GetRequiredService<ILogger<AudioConverter>>());
			var spec = $"{conv.InputSpec.Format} {conv.InputSpec.Channels}ch {conv.InputSpec.SampleFrequency}";
			if (spec == "FloatPlanarLE 1ch 44100") {
				Console.WriteLine($"mono: {fsi.Name}");
			}
			if (formats.TryGetValue(spec, out var val)) {
				formats[spec] = val + 1;
			} else {
				Console.WriteLine($"New format {spec} in song {fsi.Name}");
				formats.Add(spec, 1);
			}
		}

		foreach (var (spec, cnt) in formats) {
			Console.WriteLine($"{cnt,-4} -> {spec}");
		}
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
