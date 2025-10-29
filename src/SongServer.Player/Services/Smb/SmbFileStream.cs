using SMBLibrary;
using SMBLibrary.Client;

namespace SongServer.Services.Smb;

internal class SmbFileStream : Stream
{
	private readonly ISMBFileStore _store;
	private readonly object _handle;
	private readonly Lazy<int> _length;
	private int _position = 0;

	public SmbFileStream(ISMBFileStore store, object handle)
	{
		_store = store;
		_handle = handle;
		_length = new(() => {
			var fistat = _store.GetFileInformation(out var info, _handle, FileInformationClass.FileBasicInformation);

			if (fistat != NTStatus.STATUS_SUCCESS) {
				throw new IOException($"Failed to query file information: {fistat}");
			}

			return info.Length;
		});
	}

	public override bool CanRead => true;

	public override bool CanSeek => true;

	public override bool CanWrite => false;

	public override long Length => _length.Value;

	public override long Position
	{
		get => _position;
		set => Seek(value, SeekOrigin.Begin);
	}

	public override void Flush() => throw new NotSupportedException();

	public override int Read(byte[] buffer, int offset, int count)
	{
		var readStatus = _store.ReadFile(out var data, _handle, _position, count);
		
		if (readStatus != NTStatus.STATUS_SUCCESS) {
			throw new IOException($"Failed to read file: {readStatus}");
		}

		var bytesRead = Math.Min(data.Length, count);

		Array.Copy(data, 0, buffer, offset, bytesRead);

		_position += bytesRead;

		return bytesRead;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		_position = origin switch {
			SeekOrigin.Begin => (int)offset,
			SeekOrigin.Current => _position + (int)offset,
			SeekOrigin.End => (int)Length + (int)offset,
			_ => throw new ArgumentException("Invalid seek origin")
		};
		
		return _position;
	}

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
