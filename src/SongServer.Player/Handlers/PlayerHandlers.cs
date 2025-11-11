using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SongServer.Data;
using SongServer.Services;
using SongServer.Services.Library;

namespace SongServer.Handlers;

internal class PlayerHandlers
{
	private readonly static JsonSerializerOptions _sseSerializerOptions = new(JsonSerializerDefaults.Web) {
		WriteIndented = false,
	};

	private readonly static JsonWriterOptions _sseWriterOptions = new() {
		Indented = false,
	};

	private PlayerHandlers() {}

	public static void Map(IEndpointRouteBuilder builder)
	{
		var group = builder.MapGroup("player/");

		group.MapGet("play", Play);
		group.MapGet("pause", Pause);
		group.MapGet("stop", Stop);
		group.MapGet("advance", Advance);
		group.MapGet("enqueue", Enqueue);
		group.MapGet("", GetState);
		group.MapGet("state-stream", GetStateStream);
		group.MapGet("playlist", GetPlaylistView);
	}

	public static async Task<PlayerState> Play([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		if (!await player.Play()) {
			throw new Exception("Failed to play");
		}

		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, position, 0, 0);
	}

	public static async Task<PlayerState> Pause([FromServices]AudioPlayer player)
	{
		if (!await player.Pause()) {
			throw new Exception("Failed to pause");
		}

		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, position, 0, 0);
	}

	public static async Task<PlayerState> Stop([FromServices]AudioPlayer player)
	{
		if (!await player.Stop()) {
			throw new Exception("Failed to stop");
		}

		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, position, 0, 0);
	}

	public static async Task<PlayerState> Advance([FromServices]AudioPlayer player)
	{
		if (!await player.Advance()) {
			throw new Exception("Failed to advance");
		}

		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, position, 0, 0);
	}

	public static PlayerState GetState(AudioPlayer player)
	{
		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, position, 0, 0);
	}

	public static async Task GetStateStream([FromServices]AudioPlayer player, HttpContext httpContext, CancellationToken cancellationToken)
	{
		var writer = httpContext.Response.BodyWriter;

		// set headers for SSE
		httpContext.Response.Headers.Append("Cache-Control", "no-cache");
		httpContext.Response.Headers.ContentType = "text/event-stream";
		httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

		// send initial state similar to PlayerHub.OnConnectedAsync
		var (curState, curSong, curPos) = player.GetState();

		Handler(null, new StateChangedEventArgs() { State = curState, Song = curSong, Position = curPos });

		player.StateChanged += Handler;

		try {
			// keep the request open until the client disconnects
			await Task.Delay(Timeout.Infinite, cancellationToken);
		} catch (OperationCanceledException) {
			// client disconnected or cancellation requested
		} finally {
			player.StateChanged -= Handler;
		}

		// event handler to forward player StateChanged events as SSE
		void Handler(object? s, StateChangedEventArgs e) => _ = Task.Run(async () => {
				try {
					using var jsonWriter = new Utf8JsonWriter(writer, _sseWriterOptions);

					Encoding.UTF8.GetBytes("event: playerState\n".AsSpan(), writer);
					Encoding.UTF8.GetBytes("data: ", writer);
					JsonSerializer.Serialize(jsonWriter, e, _sseSerializerOptions);
					Encoding.UTF8.GetBytes("\n\n", writer);

					await jsonWriter.FlushAsync();
					await writer.FlushAsync();
				} catch (OperationCanceledException) {
					/* ignore */
				} catch (Exception) {
					/* ignore write errors */
				}
			}, cancellationToken);
	}

	public static async Task<Results<ProblemHttpResult, Ok<PlayerState>>> Enqueue([FromServices]AudioPlayer player, [FromServices]LibraryService library, string? artist, string? album, string? title, CancellationToken cancellationToken = default)
	{
		// find the song in the library by whatever criteria we were provided
		var songs = await library.SearchLibrary(artist, album, title);

		// if multiple songs were found, return an error
		if (songs.Count == 0) {
			return TypedResults.Problem(new() {
				Status = 422,
				Title = "No songs found matching the specified criteria.",
			});
		}

		if (songs.Count > 1) {
			return TypedResults.Problem(new() {
				Status = 422,
				Title = "Multiple songs found matching the criteria. Please be more specific.",
				Extensions = {
					{ "songCount", songs.Count }
				}
			});
		}

		player.Enqueue(new SongReference(songs[0].SourceId, songs[0].RelativePath));

		var (state, song, position) = player.GetState();

		return TypedResults.Ok(new PlayerState(state, song, position, 0, 0));
	}

	public static async Task<Results<Ok<PlaylistView>, ProblemHttpResult>> GetPlaylistView(Playlist playlist, LibraryContext context)
	{
		try {
			var (prev, cur, next) = playlist.GetCurrentView();

			// Collect all song references we need to look up
			var allRefs = prev.Append(cur).Concat(next).ToList();

			// Build expression that represents the logical OR of all song references
			Expression<Func<Song, bool>> predicate = null!;
			foreach (var r in allRefs) {
				var sourceId = r.SourceId;
				var relativePath = r.RelativePath;
				Expression<Func<Song, bool>> condition = s => s.SourceId == sourceId && s.RelativePath == relativePath;

				if (predicate == null) {
					predicate = condition;
				} else {
					predicate = CombineWithOr(predicate, condition);
				}
			}

			// Query all needed songs from the database
			var songs = await context.Songs
				.AsNoTracking()
				.Where(predicate)
				.ToListAsync();

			// Create lookup dictionaries for efficient mapping
			var songLookup = songs.ToDictionary(s => (s.SourceId, s.RelativePath));

			// Convert SongReference to SongMetadata
			var previousMetadata = prev
				.Select(r => songLookup.TryGetValue((r.SourceId, r.RelativePath), out var song) ? ToMetadata(song) : null)
				.Where(m => m != null)
				.Cast<SongMetadata>()
				.ToList();

			var currentMetadata = cur.SourceId >= 0 && songLookup.TryGetValue((cur.SourceId, cur.RelativePath), out var currentSong) ?
				ToMetadata(currentSong) : new SongMetadata(null, null, "Unknown", null, null, null, TimeSpan.Zero);

			var upcomingMetadata = next
				.Select(r => songLookup.TryGetValue((r.SourceId, r.RelativePath), out var song) ? ToMetadata(song) : null)
				.Where(m => m != null)
				.Cast<SongMetadata>()
				.ToList();

			var playlistView = new PlaylistView(previousMetadata, currentMetadata, upcomingMetadata);

			return TypedResults.Ok(playlistView);
		} catch (Exception e) {
			return TypedResults.Problem($"Error retrieving playlist status: {e.Message}");
		}
	}

	private static Expression<Func<T, bool>> CombineWithOr<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
	{
		var parameter = Expression.Parameter(typeof(T));
		var leftInvoke = Expression.Invoke(left, parameter);
		var rightInvoke = Expression.Invoke(right, parameter);
		var orExpression = Expression.OrElse(leftInvoke, rightInvoke);
		return Expression.Lambda<Func<T, bool>>(orExpression, parameter);
	}

	private static SongMetadata ToMetadata(Song song) => new(song.Performers, song.Album, song.Title, song.TrackNumber, song.TrackCount, null, song.Duration);
}
