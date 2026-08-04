namespace SongServer.Audio;

public class PauseableStream<T>(T outputStream) : Stream where T: Stream
{
	private readonly T _outputStream = outputStream;
	private readonly SemaphoreSlim _semaphore = new(1, 1);
	private bool _paused = false;
	private bool _disposed = false;

	public T UnderlyingStream => _outputStream;

	public bool Paused { get => _paused; set { if (value) { Pause(); } else { Resume(); } } }

	public override bool CanRead => _outputStream.CanRead;

	public override bool CanSeek => _outputStream.CanSeek;

	public override bool CanWrite => _outputStream.CanWrite;

	public override long Length => _outputStream.Length;

	public override long Position { get => _outputStream.Position; set => _outputStream.Position = value; }

	public long WrittenBytes { get; private set; }

	public override void Flush() => _outputStream.Flush();

	public override int Read(byte[] buffer, int offset, int count) => _outputStream.Read(buffer, offset, count);

	public override long Seek(long offset, SeekOrigin origin) => _outputStream.Seek(offset, origin);

	public override void SetLength(long value) => _outputStream.SetLength(value);

	public void Pause()
	{
		// we can only pause so hard
		if (_paused) {
			return;
		}

		// take a lock on the semaphore so Write() will block
		_semaphore.Wait();

		// and now we're paused
		_paused = true;
	}

	public void Resume()
	{
		if (!_paused) {
			return;
		}

		// release the lock
		_semaphore.Release();

		// we're no longer paused
		_paused = false;
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		// wait until we can acquire the lock
		// this could block potentially forever... hope
		// upstream doesn't have anything important to do
		_semaphore.Wait();

		try {
			_outputStream.Write(buffer, offset, count);
			WrittenBytes += count;
		} finally {
			// release the semaphore 
			_semaphore.Release();
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (_disposed) {
			return;
		}

		if (disposing) {
			_semaphore?.Dispose();
		}

		_outputStream.Dispose();

		_disposed = true;
		base.Dispose(disposing);
	}
}
