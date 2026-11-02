using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using System.Text.Json;
using SongClassifier.Billboard;
using Microsoft.Data.Sqlite;

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
		// foreach (var fn in Directory.GetFiles(@"\\vmhost.local\shared-folder\uploaded-media\", "*.zip")) {
		// 	await MediaPreparer.PrepareMedia(fn, _extracted);
		// }
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\311 - 311 (30th Anniversary).zip", _extracted);
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\Afghan Whigs - Gentlemen.zip", _extracted);
		//await MediaPreparer.PrepareMedia(@"\\vmhost.local\shared-folder\uploaded-media\Space - Spiders.zip", _extracted);
		//await PlaylistEditor.EditPlaylist();
		//await CopyPlaylist();

		await RecordStreamMetadata();
	}

	public static async Task CopyPlaylist()
	{
		const string connectionString = @"Data Source=C:\Users\MarkParker\source\repos\song-server\src\SongServer.Player\music_library.db";

		using var context = new BillboardContext();
		using var conn = new SqliteConnection(connectionString);

		await conn.OpenAsync();

		await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

		await using var insertCmd = conn.CreateCommand();
		insertCmd.Transaction = tx;
		insertCmd.CommandText = "INSERT INTO \"playlist\" (source_id, relative_path) VALUES (@source_id, @relative_path);";

		var paramSource = insertCmd.CreateParameter();
		paramSource.ParameterName = "@source_id";
		paramSource.Value = 3;
		insertCmd.Parameters.Add(paramSource);

		var paramPath = insertCmd.CreateParameter();
		paramPath.ParameterName = "@relative_path";
		insertCmd.Parameters.Add(paramPath);

		foreach (var item in context.Playlist) {
			paramPath.Value = item.Filename[(item.Filename.IndexOf('\\') + 1)..];
			await insertCmd.ExecuteNonQueryAsync();
		}

		await tx.CommitAsync();
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

	private static async Task RecordStreamMetadata(CancellationToken cancellationToken = default)
	{
		using var handler = new HttpClientHandler() {
			AutomaticDecompression = System.Net.DecompressionMethods.All	
		};
		using var httpClient = new HttpClient(handler);
		using var dbConn = new SqliteConnection("Data Source=stream_metadata.db");
		using var dbCmd  = dbConn.CreateCommand();

		await dbConn.OpenAsync(cancellationToken);

		dbCmd.CommandText = """
			CREATE TABLE IF NOT EXISTS stream_metadata (
				metadata TEXT NOT NULL,
				artist TEXT GENERATED ALWAYS AS (json_extract(metadata, '$.artist')),
				album TEXT GENERATED ALWAYS AS (json_extract(metadata, '$.album')),
				title TEXT GENERATED ALWAYS AS (json_extract(metadata, '$.title')),
				startTime INTEGER GENERATED ALWAYS AS (json_extract(metadata, '$.startTime')),
				endTime INTEGER GENERATED ALWAYS AS (json_extract(metadata, '$.endTime'))
			);

			CREATE UNIQUE INDEX IF NOT EXISTS IX_stream_metadata_startTime on stream_metadata (startTime);
			""";

		await dbCmd.ExecuteNonQueryAsync(cancellationToken);

		dbCmd.CommandText = "insert or ignore into stream_metadata (metadata) values (@metadata);";

		var mdParam   = dbCmd.Parameters.Add(new SqliteParameter("@metadata", SqliteType.Text));
		var metaUrl   = new Uri("https://us.api.iheart.com/api/v3/live-meta/stream/333/currentTrackMeta?defaultMetadata=true");
		var lastStart = 0L;

		httpClient.DefaultRequestHeaders.Clear();
		httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
		httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
		httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
		httpClient.DefaultRequestHeaders.Add("X-hostName", "webapp.US");
		httpClient.DefaultRequestHeaders.Add("X-Locale", "en-US");
		httpClient.DefaultRequestHeaders.Add("Origin", "https://radio945fm.iheart.com");
		httpClient.DefaultRequestHeaders.Add("Referer", "https://radio945fm.iheart.com/");
		httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
		httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
		httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
		httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
		httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:144.0) Gecko/20100101 Firefox/144.0");

		while (!cancellationToken.IsCancellationRequested) {
			var resp = await httpClient.GetAsync(metaUrl, cancellationToken);

			if (resp.IsSuccessStatusCode) {
				var content = await resp.Content.ReadAsStringAsync(cancellationToken);

				if (content.Length > 0) {
					try {
						using var jd = JsonDocument.Parse(content);

						var artist = jd.RootElement.GetProperty("artist").GetString();
						var album  = jd.RootElement.GetProperty("album").GetString();
						var title  = jd.RootElement.GetProperty("title").GetString();
						var start  = jd.RootElement.GetProperty("startTime").GetInt64();

						if (lastStart != start) {
							Console.WriteLine($"Now playing: [{title}] by {artist} ({album})");
							lastStart = start;
						} else {
							//Console.WriteLine($"start didn't change; title is {title}");
							Console.Write(".");
						}

						mdParam.Value = content;
						await dbCmd.ExecuteNonQueryAsync(cancellationToken);
					} catch (Exception e) {
						Console.Error.WriteLine($"Failed to parse respose: {e.Message}");
						Console.Error.WriteLine(content);
					}
				} else {
					//Console.WriteLine("No content.");
					Console.Write("_");
				}
			} else {
				Console.Error.WriteLine($"Not a success status code: {resp.StatusCode} {resp.ReasonPhrase}");
			}

			await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
		}

		await dbConn.CloseAsync();
	}
}

internal record SongMetadata(string Artist, string Album, string Title, int? Track);
