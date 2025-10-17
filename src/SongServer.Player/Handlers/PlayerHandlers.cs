using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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

		group.MapGet("play", PlayerHandlers.Play);
		group.MapGet("pause", PlayerHandlers.Pause);
		group.MapGet("stop", PlayerHandlers.Stop);
		group.MapGet("advance", PlayerHandlers.Advance);
		group.MapGet("enqueue", PlayerHandlers.Enqueue);
		group.MapGet("", PlayerHandlers.GetState);
		group.MapGet("state-stream", PlayerHandlers.GetStateStream);
	}

	public static async Task<PlayerState> Play([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		var tcs = new TaskCompletionSource<bool>();

		player.StateChanged += ChangeHandler;

		player.Play();

		await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);

		player.StateChanged -= ChangeHandler;

		var (state, song, position) = player.GetState();

		return new PlayerState(state, song, 0, 0);

		void ChangeHandler(object? s, StateChangedEventArgs e)
		{
			if (e.State.HasValue) {
				tcs.SetResult(true);
			}
		}
	}

	public static void Pause([FromServices]AudioPlayer player) => player.Pause();

	public static void Stop([FromServices]AudioPlayer player) => player.Stop();

	public static PlayerState Advance([FromServices]AudioPlayer player, CancellationToken cancellationToken = default)
	{
		player.Advance();

		return new PlayerState(State.Playing, null, 0, 0);//player.GetState();
	}

	public static Task<PlayerState> GetState(AudioPlayer player, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new PlayerState(State.Playing, null, 0, 0));//player.GetState();
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
		var songs = await library.FindSongsAsync(artist, album, title);

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

		player.Enqueue(songs[0].Url);

		//return TypedResults.Ok(player.GetState());
		return TypedResults.Ok(new PlayerState(State.Stopped, null, 0, 0));
	}
}
