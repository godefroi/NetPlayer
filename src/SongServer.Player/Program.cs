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
using Microsoft.Extensions.FileProviders;

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
		builder.Services.AddTransient<FileInformationRetriever>();
		builder.Services.AddDbContextFactory<LibraryContext>();

		builder.Services.AddHostedService<AudioPlayerService>();
		builder.Services.AddHostedService<PlayerHubBroadcaster>();
		if (builder.Configuration.GetSection(StreamMetadataOptions.SectionName).Exists()) {
			builder.Services.AddHostedService<StreamMetadataService>();
		}

		builder.Services.AddCors();

		builder.Services.AddSignalR();

		var app = builder.Build();

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

		await ExecutePendingMigrations(app.Services);

		//Console.WriteLine(builder.Environment.WebRootPath);
		// enable static file hosting (for the UI)
		//var fileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "wwwroot")); // TODO: the first wwwroot should be something better, like _ui or something

//app.MapStaticAssets(@"C:\Users\MarkParker\source\repos\song-server\src\SongServer.Player\wwwroot\SongServer.Ui.staticwebassets.endpoints.json");
		// app.UseRouting();
		// app.UseDefaultFiles(new DefaultFilesOptions() {
		// 	FileProvider = fileProvider,
		// 	DefaultFileNames = ["index.html"],
		// });
		// var sfo = new StaticFileOptions() {
		// 	FileProvider = fileProvider,
		// };
		// sfo.ContentTypeProvider.TryAddMapping(".dat", "application/octet-stream");
		// app.UseStaticFiles(sfo);

		app.MapStaticAssets();
		app.MapFallbackToFile("index.html");

		PlayerHandlers.Map(app);
		LibraryHandlers.Map(app);
		PlaylistHandlers.Map(app);

		app.MapHub<PlayerHub>("/player/hub");

		// add a fallback route for blazor client-side routing
		//app.MapFallbackToFile("index.html");
		// app.MapFallbackToFile("index.html", new StaticFileOptions() {
		// 	FileProvider = fileProvider,
		// });

		app.UseCors(builder => builder
			.AllowAnyOrigin()
			.AllowAnyHeader()
			.AllowAnyMethod());

		var logger = app.Services.GetRequiredService<ILogger<Program>>();
		var pOpts  = app.Services.GetRequiredService<IOptions<PlayerOptions>>().Value;

		SdlAudioSystem.Initialize(
			app.Services.GetRequiredService<ILogger<SdlAudioSystem>>(),
			app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

		FFmpegSystem.Initialize(app.Services.GetRequiredService<ILogger<FFmpegSystem>>(), pOpts.FFmpegLibraryPath);

		await app.RunAsync();

		await SdlAudioSystem.Shutdown();
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
