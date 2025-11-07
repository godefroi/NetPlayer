using System;
using SMBLibrary.Client;

namespace SongServer.Services.Smb;

public class SmbFileAbstraction(ISMBFileStore store, object handle) : TagLib.File.IFileAbstraction, IDisposable
{
	private ISMBFileStore _store = store;
	private object? _handle = handle;
	private SmbFileStream? _readStream = null;
	private bool _disposed = false;

	public string Name => throw new NotImplementedException();

	public Stream ReadStream => _readStream ??= new SmbFileStream(_store, _handle ?? throw new ObjectDisposedException(nameof(_handle)));

	public Stream WriteStream => throw new NotSupportedException("Writing to SMB files is not supported");

	public void CloseStream(Stream stream)
	{
		if (stream == _readStream) {
			_readStream?.Dispose();
			_readStream = null;
		}
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		_readStream?.Dispose();
		_readStream = null;
		_store.CloseFile(_handle);
		_disposed = true;

		GC.SuppressFinalize(this);
	}
}
