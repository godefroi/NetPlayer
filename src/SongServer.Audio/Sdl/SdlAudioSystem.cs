using Microsoft.Extensions.Logging;
using SDL3;

namespace SongServer.Audio.Sdl;

public class SdlAudioSystem
{
	private static ILogger? _logger = null;
	private static CancellationTokenSource? _eventCts;
	private static Task _eventTask = Task.CompletedTask;

	private SdlAudioSystem() {}

	public static void Initialize(ILogger<SdlAudioSystem> logger, CancellationToken cancellationToken)
	{
		_logger ??= logger;
		_eventCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_eventTask = HandleSdlEvents(_eventCts.Token);

		// we'll handle our own ctrl-c, thank you very much
		SDL.SetHint(SDL.Hints.NoSignalHandlers, "1");

		// we need to initialize the SDL audio subsystem (which also initializes the events subsystem)
		if (!SDL.InitSubSystem(SDL.InitFlags.Audio)) {
			var err = SDL.GetError();
			throw new Exception($"Unable to initialize SDL audio subsystem: {err}");
		}
	}

	public static async Task Shutdown()
	{
		_logger?.LogInformation("Terminating SDL event handling...");

		_eventCts?.Cancel();
		await _eventTask;
		_eventCts?.Dispose();
		_eventCts = null;

		_logger?.LogInformation("Shutting down SDL subsystems...");

		// shut down the audio subsystem (and events too)
		SDL.QuitSubSystem(SDL.InitFlags.Audio);
		SDL.QuitSubSystem(SDL.InitFlags.Events);
		SDL.Quit();

		_logger?.LogInformation("SDL shutdown complete.");
	}

	private static async Task HandleSdlEvents(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested) {
			try {
				while (SDL.PollEvent(out var ev)) {
					// We ain't got no events... we don't need no events. I don't have to show you any stinking events!
					_logger?.LogInformation("SDL event of type {event}", (SDL.EventType)ev.Type);
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
