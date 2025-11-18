namespace SongServer.Services.Smb;

internal class SmbFileAbstraction(SmbFileStream stream, string name, bool ownsStream = true) : TagLib.File.IFileAbstraction, IDisposable
{
	private SmbFileStream? _readStream = stream;
	private readonly bool _ownsStream = ownsStream;
	private bool _disposed = false;

	public string Name => name;

	public Stream ReadStream => _readStream!;

	public Stream WriteStream => throw new NotSupportedException("Writing to SMB files is not supported");

	public void CloseStream(Stream stream)
	{
		if (stream == _readStream && _ownsStream) {
			_readStream?.Dispose();
			_readStream = null;
		}
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		if (_ownsStream) {
			_readStream?.Dispose();
		}

		_disposed = true;

		GC.SuppressFinalize(this);
	}
}
