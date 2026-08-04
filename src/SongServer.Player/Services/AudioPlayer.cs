using SongServer.Services.Library;

namespace SongServer.Services;

internal abstract class AudioPlayer(ILogger logger, FileInformationRetriever fileInfoRetriever) {
	private readonly Lock _stateLock = new();
	private readonly ILogger _logger = logger;
	private readonly Queue<SongReference> _queue = [];
	private readonly FileInformationRetriever _fileInfoRetriever = fileInfoRetriever;
	private Playlist _list = Playlist.Empty;
	private SongMetadata? _currentSong = null;
	private TimeSpan _currentPosition = TimeSpan.FromSeconds(-1);
	private TimeSpan _lastReportedPos = TimeSpan.FromSeconds(-1);
	private State _currentState = State.Stopped;

	public event EventHandler<StateChangedEventArgs>? StateChanged;

	public event EventHandler? PlaylistStateChanged;

	public async Task<bool> Play()
	{
		State state;

		lock (_stateLock) {
			state = _currentState;
		}

		_logger.LogInformation("Play() called. State: {state}", state);

		return state switch {
			State.Playing => true, // nothing to do if we're already playing
			State.Paused => await Resume(),
			State.Stopped => await Advance(),
			_ => throw new InvalidOperationException($"Unknown state {state}"),
		};
	}

	public abstract Task<bool> Pause();

	public abstract Task<bool> Stop();

	public async Task<bool> Advance()
	{
		// if we have no songs in the queue, then put the next song from
		// the playlist into the queue, if there are any songs in it
		if (_queue.Count == 0 && _list.Count > 0) {
			// add the next song to the queue
			_queue.Enqueue(_list.Next());

			// we advanced to the next song, so we should let anyone who cares know about that
			PlaylistStateChanged?.Invoke(this, EventArgs.Empty);
		}

		// now, play the next song in the queue
		if (_queue.Count > 0) {
			_logger.LogDebug("Advancing to the next song in the queue");
			// the only difficulty here is that our implementation might not provide
			// us with the current media's metadata; we're going to have to take care
			// of that ourselves
			// TODO: do that by loading the tag out of the file and verifying that we actually staretd playing
			try {
				return await Play(_queue.Dequeue());
			} catch (Exception e) {
				_logger.LogError(e, "Error while calling Play() on player.");
				throw;
			}
		} else {
			_logger.LogWarning("Cannot advance, nothing to play!");
			return false;
		}
	}

	public void SetPlaylist(Playlist list)
	{
		lock (_stateLock) {
			_list = list;
		}
	}

	public (State State, SongMetadata? Song, TimeSpan? Position) GetState()
	{
		lock (_stateLock) {
			return (_currentState, _currentSong, _currentPosition);
		}
	}

	public void Enqueue(SongReference song)
	{
		lock (_stateLock) {
			_queue.Enqueue(song);
		}
	}

	protected void OnPlayerStateChanged(State state, SongMetadata? song = null)
	{
		TimeSpan? curPos;

		lock (_stateLock) {
			switch (state) {
				case State.Stopped:
					// we were playing (probably?), now we're not
					_currentPosition = TimeSpan.Zero;
					_currentSong = null;
					break;

				case State.Playing:
					// we weren't playing (probably?), now we are
					_currentSong = song ?? _currentSong;
					break;

				default:
					// paused probably
					break;
			}

			if (_currentState != state) {
				_logger.LogInformation("Player state changed from {oldState} to {newState}", _currentState, state);
			}

			_currentState = state;
			curPos = _currentPosition;
		}

		if (song != null) {
			_logger.LogInformation("State updated to {state}, current Song: {Artist} - {Title} from {Album} (Track {TrackNumber}/{TrackTotal}) - Duration: {Duration}",
				state,
				string.Join(", ", song.Artists ?? Enumerable.Empty<string>()),
				song.Title,
				song.Album,
				song.TrackNumber,
				song.TrackTotal,
				song.Duration);
		}

		StateChanged?.Invoke(this, new StateChangedEventArgs() {
			State = state,
			Position = curPos,
			Song = song
		});
	}

	protected async Task OnPlaybackFinished()
	{
		if (!await Advance()) {
			OnPositionChanged(TimeSpan.Zero);
			OnPlayerStateChanged(State.Stopped);
		}
	}

	protected void OnPositionChanged(TimeSpan position)
	{
		var curState = State.Stopped;
		var sendPos = false;

		lock (_stateLock) {
			// always keep track of our most recent position
			_currentPosition = position;

			// if our position has sufficiently changed since we reported, then report
			if (Math.Abs((position - _lastReportedPos).TotalSeconds) > 1.0) {
				_lastReportedPos = position;
				curState = _currentState;
				sendPos = true;
			}
		}

		// if we need to raise the event, then do that
		// maybe we want to do this on a different thread...?
		if (sendPos) {
			StateChanged?.Invoke(this, new StateChangedEventArgs() {
				State = curState,
				Position = position,
				Song = _currentSong
			});
		}
	}

	protected abstract Task<bool> Resume();

	protected abstract Task<bool> Play(SongReference song);

	protected Stream GetStream(Data.Song song) => _fileInfoRetriever.OpenFileStream(song.Url);
}
