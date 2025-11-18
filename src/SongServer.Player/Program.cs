using SongServer.Audio.FFmpeg;
using SongServer.Configuration;
using Microsoft.EntityFrameworkCore;
using SongServer.Data;
using SongServer.Handlers;
using SongServer.Services;
using SongServer.Services.Smb;
using SongServer.Audio.Sdl;
using Microsoft.Extensions.Options;
using SongServer.Services.Library;

namespace SongServer;

internal class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection(PlayerOptions.SectionName));
		builder.Services.Configure<StreamMetadataOptions>(builder.Configuration.GetSection(StreamMetadataOptions.SectionName));
		builder.Services.Configure<SmbConnectionOptions>(builder.Configuration.GetSection(SmbConnectionOptions.SectionName));

		builder.Services.AddSingleton<SmbFileClient>();
		//builder.Services.AddSingleton<AudioPlayer, BassAudioPlayer>();
		builder.Services.AddSingleton<AudioPlayer, FFmpegAudioPlayer>();
		builder.Services.AddSingleton<LibraryService>();
		builder.Services.AddSingleton(sp => {
			using var context = sp.GetRequiredService<IDbContextFactory<LibraryContext>>().CreateDbContext();
			var playlistSongs = context.Playlist
				.AsNoTracking()
				.AsEnumerable()
				.Select(s => new SongReference(s.SourceId, s.RelativePath));

			var playlist = new Playlist(playlistSongs);

			return playlist;
		});
		builder.Services.AddTransient<FileInformationRetriever>();
		builder.Services.AddDbContextFactory<LibraryContext>();

		builder.Services.AddHostedService<AudioPlayerService>();
		builder.Services.AddHostedService<PlayerHubBroadcaster>();
		if (builder.Configuration.GetSection(StreamMetadataOptions.SectionName).Exists()) {
			builder.Services.AddHostedService<StreamMetadataService>();
		}

		builder.Services.AddCors();

		builder.Services.AddSignalR();

		var app    = builder.Build();
		var logger = app.Services.GetRequiredService<ILogger<Program>>();
		var pOpts  = app.Services.GetRequiredService<IOptions<PlayerOptions>>().Value;

		if (args.Length != 0) {
			switch (args[0]) {
				case "showoutputs":
					//BassAudioPlayer.DisplayOutputs();
					SdlAudioSystem.Initialize(app.Services.GetRequiredService<ILogger<SdlAudioSystem>>(), CancellationToken.None);
					SdlAudioDevice.ListAudioDevices();
					await SdlAudioSystem.Shutdown();
					return;
			}
		}

		// ffmpeg files (for windows) come from https://www.gyan.dev/ffmpeg/builds/

		await ExecutePendingMigrations(app.Services);

		// to host the frontend, publish the UI project into this directory, like so:
		// (from the UI dir) dotnet publish -c Debug -o ../SongServer.Player
		// (or from the player dir) dotnet publish ../SongServer.Ui -c Debug -o .
		// this should result in the manifest (the .json file) being in the AppContext.BaseDirectory folder
		// and a wwwroot/ folder underneath the BaseDirectory folder
		// (and the wwwroot folder just so happens to be the web root path)
		logger.LogInformation("Web root path: {webRoot}", app.Environment.WebRootPath);
		if (File.Exists(Path.Combine(AppContext.BaseDirectory, "SongServer.Ui.staticwebassets.endpoints.json"))) {
			logger.LogInformation("Mapping static web assets for frontend UI");
			app.MapStaticAssets("SongServer.Ui.staticwebassets.endpoints.json");
			app.MapFallbackToFile("index.html");
		}

		PlayerHandlers.Map(app);
		LibraryHandlers.Map(app);
		PlaylistHandlers.Map(app);

		app.MapHub<PlayerHub>("/player/hub");

		app.UseCors(builder => builder
			.AllowAnyOrigin()
			.AllowAnyHeader()
			.AllowAnyMethod());

		SdlAudioSystem.Initialize(
			app.Services.GetRequiredService<ILogger<SdlAudioSystem>>(),
			app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

		FFmpegSystem.Initialize(app.Services.GetRequiredService<ILogger<FFmpegSystem>>(), pOpts.FFmpegLibraryPath);

		await app.RunAsync();

		await SdlAudioSystem.Shutdown();
	}

	private static async Task RemoveBadDuplicatedSongs(WebApplication app)
	{
		using var scope = app.Services.CreateScope();
		using var ctx = scope.ServiceProvider.GetRequiredService<LibraryContext>();

		foreach (var g in ctx.Songs.AsEnumerable().GroupBy(s => s.Url)) {
			var gCnt = g.Count();

			if (gCnt == 1) {
				continue;
			} else if (gCnt != 2) {
				Console.Error.WriteLine($"Group has count {gCnt}: {g.Key}");
				continue;
			}

			var sCnt = g.Count(s => s.RelativePath.OriginalString.Contains("%20"));

			if (sCnt != 1) {
				Console.Error.WriteLine($"Group has %20 count of {sCnt}: {g.Key}");
				continue;
			}

			ctx.Songs.Remove(g.Single(s => s.RelativePath.OriginalString.Contains("%20")));
		}

		var rCnt = await ctx.SaveChangesAsync();

		Console.WriteLine($"Removed {rCnt} duplicated songs.");
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
}
