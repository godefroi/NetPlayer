namespace SongServer.Services;

internal abstract class AudioPlayer(ILogger logger)
{
	private readonly Lock _stateLock = new();
	private readonly ILogger _logger = logger;
	private readonly Queue<SongReference> _queue = [];
	private Playlist _list = Playlist.Empty;
	private PlaylistState _state = PlaylistState.Empty;
	private SongMetadata? _currentSong = null;
	private TimeSpan _currentPosition = TimeSpan.FromSeconds(-1);
	private State _currentState = State.Stopped;

	public event EventHandler<StateChangedEventArgs>? StateChanged;

	public async Task<bool> Play()
	{
		State state;

		lock (_stateLock) {
			state = _currentState;
		}

		_logger.LogInformation("Play() called. State: {state}", state);

		switch (state) {
			case State.Playing:
				// nothing to do if we're already playing
				return true;
			case State.Paused:
				return await Resume();
			case State.Stopped:
				return await Advance();
			default:
				throw new InvalidOperationException($"Unknown state {state}");
		}
	}

	public abstract Task<bool> Pause();

	public abstract Task<bool> Stop();

	public async Task<bool> Advance()
	{
		// if we have no songs in the queue, then put the next song from
		// the playlist into the queue, if there are any songs in it
		if (_queue.Count == 0 && _list.Entries.Count > 0) {
			// check if the index is now out of bounds
			if (++_state.CurrentIndex >= _list.Entries.Count) {
				_state.CurrentIndex = 0;
			}

			// add the next song to the queue
			_queue.Enqueue(_list.Entries[_state.CurrentIndex]);
		}

		// now, play the next song in the queue
		if (_queue.Count > 0) {
			_logger.LogDebug("Advancing to the next song in the queue");
			// the only difficulty here is that our implementation might not provide
			// us with the current media's metadata; we're going to have to take care
			// of that ourselves
			// TODO: do that by loading the tag out of the file and verifying that we actually staretd playing
			return await Play(_queue.Dequeue());
		} else {
			_logger.LogWarning("Cannot advance, nothing to play!");
			return false;
		}
	}

	public void SetPlaylist(Playlist list, PlaylistState state)
	{
		lock (_stateLock) {
			_list  = list;
			_state = state;
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

	protected void OnPlayerStateChanged(State state)
	{
		TimeSpan? curPos;
		SongMetadata? curSong;

		lock (_stateLock) {
			_currentState = state;
			curPos = _currentPosition;
			curSong = _currentSong;
		}

		StateChanged?.Invoke(this, new StateChangedEventArgs() {
			State    = state,
			Position = curPos,
			Song     = curSong
		});
	}

	protected async Task OnPlaybackFinished()
	{
		if (!await Advance()) {
			OnPositionChanged(TimeSpan.Zero);
			OnPlayerStateChanged(State.Stopped);
		}
	}

	protected void OnMediaMetadataAvailable(SongMetadata song)
	{
		State curState;
		TimeSpan curPos;

		lock (_stateLock) {
			_currentSong = song;
			curState = _currentState;
			curPos = _currentPosition;
		}

		StateChanged?.Invoke(this, new StateChangedEventArgs() {
			Song = song,
			State = curState,
			Position = curPos
		});

		_logger.LogInformation("Current Song: {Artist} - {Title} from {Album} (Track {TrackNumber}/{TrackTotal}) - Duration: {Duration}",
			string.Join(", ", song.Artists ?? Enumerable.Empty<string>()),
			song.Title,
			song.Album,
			song.TrackNumber,
			song.TrackTotal,
			song.Duration);
	}

	protected void OnPositionChanged(TimeSpan position)
	{
		var curState = State.Stopped;
		var sendPos = false;

		lock (_stateLock) {
			// only send(/update) the event if position changed by enough
			if (Math.Abs((position - _currentPosition).TotalSeconds) > 1.0) {
				_currentPosition = position;
				curState = _currentState;
				sendPos = true;
			}
		}

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

	internal class PlaylistState
	{
		public static PlaylistState Empty { get; } = new()
		{
			Path = string.Empty,
			Hash = string.Empty
		};

		public required string Path { get; init; }
		public required string Hash { get; init; }
		public int? ShuffleSeed { get; init; }
		public int CurrentIndex { get; set; } = -1;
	}
}
