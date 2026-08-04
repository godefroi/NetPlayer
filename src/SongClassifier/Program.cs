using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using System.Text.Json;
using SongClassifier.Billboard;

namespace SongClassifier;

internal class Program
{
	private const string _audioRoot = @"\\vmhost.local\media\Audio";
	private const string _cdsRoot = @"\\vmhost.local\media\Audio\Full CDs";
	private const string _extracted = @"\\vmhost.local\shared-folder\uploaded-media\extracted";

	private readonly static JsonSerializerOptions _jsonOptions = new() {
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
	};

	private readonly static HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase) {
		".mp3",
		".flac",
		".opus",
		".m4a"
	};

	public static async Task Main(string[] args)
	{
		foreach (var fn in Directory.GetFiles(@"\\vmhost.local\shared-folder\uploaded-media\", "*.zip")) {
			await MediaPreparer.PrepareMedia(fn, _extracted);
		}
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\311 - 311 (30th Anniversary).zip", _extracted);
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\Afghan Whigs - Gentlemen.zip", _extracted);
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\Space - Spiders.zip", _extracted);
		//await PlaylistEditor.EditPlaylist();
		//await CopyPlaylist();

		//await RecordStreamMetadata();
	}

	private static async Task MatchUpSongsWithBillboardData()
	{
		using var context = new BillboardContext();

		if (!await context.Files.AnyAsync()) {
			await LoadFiles(context, _cdsRoot);
		}

		var fileArtists = await context.Files
			.Where(a => !a.Artist.Contains(", "))
			.Select(f => f.Artist)
			.Distinct()
			.ToHashSetAsync();

		foreach (var artist in fileArtists) {
			var artistFiles = await context.Files.Where(f => f.Artist == artist).Select(f => f.Title).ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

			var rankings = await context.Radio
				.Select(r => new { r.Artist, r.Title/*, r.Rank*/ })
				.Union(context.Hot100
					.Select(h => new { h.Artist, h.Title/*, h.Rank*/ })
				)
				.Where(e => e.Artist == artist)
				.ToListAsync();

			if (rankings.Count == 0) {
				Console.WriteLine($"{artist} -> [NO RANKINGS]");
			} else {
				Console.WriteLine($"{artist} -> {artistFiles.Count} files, {rankings.Count} rankings");

				foreach (var r in rankings.Where(r => artistFiles.Contains(r.Title))) {
					Console.WriteLine($"  {r.Title} (MATCHED)");
				}

				foreach (var r in rankings.Where(r => !artistFiles.Contains(r.Title))) {
					Console.WriteLine($"  {r.Title} (UNMATCHED)");
				}
			}
		}

		// var missingArtists = await context.Files
		// 	.Select(f => f.Artist)
		// 	.Except(context.Radio.Select(r => r.Artist).Union(context.Hot100.Select(r => r.Artist)))
		// 	.Where(a => !a.Contains(", "))
		// 	.ToListAsync();

		// foreach (var artist in missingArtists) {
		// 	Console.WriteLine(artist);
		// }

		// var highestPeak = await context.Radio
		// 	.Where(r => r.Artist == "Artist Name" && r.Title == "Song Title")
		// 	.MaxAsync(r => r.PeakPos);

		// var databaseArtists = await context.Radio.Select(r => r.Artist).ToHashSetAsync();
		// var fileArtists     = GetSongs().Select(s => s.Artist).ToHashSet();

		// foreach (var missing in fileArtists.Where(a => !databaseArtists.Contains(a)).Order()) {
		// 	Console.WriteLine(missing);
		// }
	}

	private static async Task LoadFiles(BillboardContext context, string fromPath, string relativeTo = _audioRoot)
	{
		// delete all rows
		await context.Files.ExecuteDeleteAsync();

		foreach (var (filename, meta) in GetSongs(fromPath)) {
			// if (/*metadata.Artist == "Matchbox Twenty" &&*/ meta.Title == "Sgt Pepper's Lonely Hearts Clu") {
			// 	Console.WriteLine(filename);
			// }

			var relativeFn = Path.GetRelativePath(relativeTo, filename);

			context.Files.Add(new SongFile(meta.Artist, meta.Album, meta.Title, relativeFn) {
				Track = meta.Track,
			});
		}

		await context.SaveChangesAsync();
	}

	private static async Task ClassifyWithAI()
	{
		var configuration = new ConfigurationBuilder()
			.SetBasePath(Directory.GetCurrentDirectory())
			.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
			.AddUserSecrets<Program>()
			.Build();

		// Set up dependency injection
		var services = new ServiceCollection();

		// Configure Azure OpenAI client
		var azureOpenAIConfig = configuration.GetSection("AzureOpenAI");
		var azureClient = new AzureOpenAIClient(new Uri(azureOpenAIConfig["Endpoint"]!), new Azure.AzureKeyCredential(azureOpenAIConfig["ApiKey"]!));

		services.AddChatClient(azureClient.GetChatClient(azureOpenAIConfig["DeploymentName"]!).AsIChatClient());

		var serviceProvider = services.BuildServiceProvider();
		var chatClient = serviceProvider.GetRequiredService<IChatClient>();

		var songChunks = GetSongs(_cdsRoot)
			.Select(t => t.Metadata)
			.Chunk(25);

		var messages = new[] {
			new ChatMessage(ChatRole.System, """
				I will provide a list of songs in a JSON format like this:

				[
					{ "artist": "artist here", "album": "album here", "title": "title here" },
					{ "artist": "artist here", "album": "album here", "title": "title here" },
					{ "artist": "artist here", "album": "album here", "title": "title here" }
				]

				You will return a JSON list of songs (in the same JSON format) that were popular and
				commonly played on the radio in various markets in the USA. You should return only
				songs that received very high play time; songs that would be familiar to someone who
				grew up during the relevant time period and was not necessarily a fan of the specific
				band. If none of the input songs received wide play, return an empty array. The output
				should represent the popular songs of the relevant time, not the most popular songs
				among those in the input.

				Do not include markdown code fences in your output.
				"""),
			new ChatMessage(ChatRole.User, string.Empty),
		};

		var popular = new List<SongMetadata>();

		foreach (var chunk in songChunks) {
			messages[1].Contents.Clear();
			messages[1].Contents.Add(new TextContent(JsonSerializer.Serialize(chunk, _jsonOptions)));
			Console.WriteLine(messages[1].Text);
			try {
				Console.WriteLine("Calling OpenAI...");
				var response = await chatClient.GetResponseAsync(messages/*, new ChatOptions() { ResponseFormat = ChatResponseFormat.Json }*/);
				Console.WriteLine(response.Text);
				var songs = JsonSerializer.Deserialize<List<SongMetadata>>(response.Text);

				if (songs != null)
				{
					Console.WriteLine($"Added {songs.Count} songs.");
					popular.AddRange(songs);
				}
			} catch (Exception ex) {
				Console.WriteLine($"Error classifying songs: {ex.Message}");
			}

			if (popular.Count > 10) {
				break;
			}
		}

		using (var fs = File.OpenWrite("popular_songs.json")) {
			JsonSerializer.Serialize(fs, popular, _jsonOptions);
		}
	}

	private static IEnumerable<(string Filename, SongMetadata Metadata)> GetSongs(string root) => Directory
		.GetFiles(root, "*.*", SearchOption.AllDirectories)
		.Select(s => new FileInfo(s))
		.Where(f => _extensions.Contains(f.Extension))
		.Select(f => {
			try {
				using var tag = TagLib.File.Create(f.FullName);
				return (f.FullName, new SongMetadata(tag.Tag.FirstPerformer, tag.Tag.Album, tag.Tag.Title, tag.Tag.Track == 0 ? null : (int)tag.Tag.Track));
			} catch (Exception e) {
				throw new Exception($"Error loading tag from file {f.FullName}", e);
			}
		});
}

internal record SongMetadata(string Artist, string Album, string Title, int? Track);
