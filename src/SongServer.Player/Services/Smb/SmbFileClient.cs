using SMBLibrary.Client;

namespace SongServer.Services.Smb;

public class SmbFileClient : IDisposable
{
	private readonly ILogger _logger;
	private readonly SMB2Client _smb = new();
	private readonly Dictionary<string, ISMBFileStore> _shares = [];
	private bool _disposed = false;

	public SmbFileClient(ILogger<SmbFileClient> logger)
	{
		_logger = logger;

		if (!_smb.Connect("vmhost.local", SMBLibrary.SMBTransportType.DirectTCPTransport)) {
			throw new Exception("unable to connect to SMB server");
		}

		var listat = _smb.Login(string.Empty, string.Empty, string.Empty);
		if (listat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
			throw new Exception($"failed to login with status {listat}");
		}

		var store = _smb.TreeConnect("media", out var tcstat);
		if (tcstat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
			throw new Exception($"failed to connect to share with status {tcstat}");
		}
	}

	public (ISMBFileStore Store, object Handle) OpenRead(string share, string path)
	{
		_logger.LogInformation("Retrieve file [{path}] from share {share}", path, share);

		if (!_shares.TryGetValue(share, out var store)) {
			store = _smb.TreeConnect("media", out var tcstat);

			if (tcstat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
				throw new Exception($"failed to connect to share with status {tcstat}");
			}

			_shares.Add(share, store);

			_logger.LogInformation("Connected to share {share} on SMB server {server}", share, "(unknown)");
		}

		var cfstat = store.CreateFile(out var handle, out var status, path, SMBLibrary.AccessMask.GENERIC_READ | SMBLibrary.AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal, SMBLibrary.ShareAccess.Read, SMBLibrary.CreateDisposition.FILE_OPEN, SMBLibrary.CreateOptions.FILE_NON_DIRECTORY_FILE | SMBLibrary.CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

		if (status == SMBLibrary.FileStatus.FILE_DOES_NOT_EXIST) {
			throw new FileNotFoundException();
		}

		if (cfstat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
			throw new IOException($"Unable to open file {path} for reading on share {share}");
		}

		if (status != SMBLibrary.FileStatus.FILE_OPENED) {
			throw new IOException("Failure status");
		}

		return (store, handle);
	}

	public (ISMBFileStore Store, object Handle) OpenRead(string uncPath)
	{
		if (!uncPath.StartsWith("\\\\")) {
			throw new InvalidOperationException("Invalid UNC path (paths should start with \\\\)");
		}

		var parts = uncPath[2..].Split('\\', 3);

		if (parts.Length < 3) {
			throw new InvalidOperationException("Invalid UNC path");
		}

		// TODO: here we're just gonna throw away the server name...
		return OpenRead(parts[1], parts[2]);
	}

	public void Dispose()
	{
		if (_disposed ) {
			return;
		}

		if (_smb.IsConnected) {
			_smb.Logoff();
			_smb.Disconnect();
		}

		GC.SuppressFinalize(this);
	}
}
