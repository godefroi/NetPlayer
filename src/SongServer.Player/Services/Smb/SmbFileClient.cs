using Microsoft.Extensions.Options;
using SMBLibrary;
using SMBLibrary.Client;
using SongServer.Configuration;

namespace SongServer.Services.Smb;

public class SmbFileClient(IOptions<SmbConnectionOptions> options, ILogger<SmbFileClient> logger) : IDisposable
{
	private readonly SmbConnectionOptions _options = options.Value;
	private readonly ILogger _logger = logger;
	private readonly Dictionary<string, SMB2Client> _clients = new(StringComparer.InvariantCultureIgnoreCase);
	private readonly Dictionary<string, ISMBFileStore> _shares = new(StringComparer.InvariantCultureIgnoreCase);
	private bool _disposed = false;

	public Stream OpenStream(string uncPath)
	{
		var (server, share, path) = ParseUnc(uncPath);
		
		return OpenStream(server, share, path);
	}

	public Stream OpenStream(string server, string share, string path)
	{
		var (store, handle) = OpenRead(server, share, path);

		return new SmbFileStream(store, handle);
	}

	public (ISMBFileStore Store, object Handle) OpenRead(string uncPath)
	{
		var (server, share, path) = ParseUnc(uncPath);
		return OpenRead(server, share, path);
	}

	public (ISMBFileStore Store, object Handle) OpenRead(string server, string share, string path)
	{
		_logger.LogDebug("Open file [{path}] from share {share} on server {server}", path, share, server);

		var smb    = GetSmbClient(server);
		var store  = GetFileStore(smb, share);
		var cfstat = store.CreateFile(out var handle, out var status, path, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

		if (status == FileStatus.FILE_DOES_NOT_EXIST) {
			throw new FileNotFoundException();
		}

		if (cfstat != NTStatus.STATUS_SUCCESS) {
			throw new IOException($"Unable to open file {path} for reading on share {share}");
		}

		if (status != FileStatus.FILE_OPENED) {
			throw new IOException($"Failure status: {status}");
		}

		return (store, handle);
	}

	public IEnumerable<string> EnumerateFiles(string uncPath)
	{
		var (server, share, path) = ParseUnc(uncPath);
		return EnumerateFiles(server, share, path);
	}

	public IEnumerable<string> EnumerateFiles(string server, string share, string path)
	{
		var shareRw  = ShareAccess.Read | ShareAccess.Write;
		var readAcc  = AccessMask.GENERIC_READ;
		var faDir    = SMBLibrary.FileAttributes.Directory;
		var fileOpen = CreateDisposition.FILE_OPEN;
		var coDirFl  = CreateOptions.FILE_DIRECTORY_FILE;
		var smb      = GetSmbClient(server);
		var store    = GetFileStore(smb, share);
		var dirs     = new Queue<string>([path]);

		while (dirs.Count > 0) {
			object? dirHandle = default;

			try {
				// get the next directory we're examining
				var curDir = dirs.Dequeue();

				// get a handle to the directory
				var cfStatus = store.CreateFile(out dirHandle, out var dirStatus, curDir, readAcc, faDir, shareRw, fileOpen, coDirFl, null);

				if (cfStatus != NTStatus.STATUS_SUCCESS) {
					throw new IOException($"Unable to open handle to directory {curDir} on share {share}");
				}

				var qdStatus = store.QueryDirectory(out var fileList, dirHandle, "*", FileInformationClass.FileDirectoryInformation);

				if (qdStatus != NTStatus.STATUS_NO_MORE_FILES) {
					throw new IOException($"Unable to query directory {curDir} on share {share}");
				}

				foreach (var fi in fileList.OfType<FileDirectoryInformation>()) {
					if (fi.FileAttributes.HasFlag(SMBLibrary.FileAttributes.Directory)) {
						if (fi.FileName != "." && fi.FileName != "..") {
							dirs.Enqueue(Path.Combine(curDir, fi.FileName));
						}
					} else {
						// curDir will be rooted at the share
						// we want to return a path relative to the source path
						//yield return Path.Combine(curDir, fi.FileName);
						yield return Path.GetRelativePath(path, Path.Combine(curDir, fi.FileName));
					}
				}
			} finally {
				if (dirHandle != null) {
					store.CloseFile(dirHandle);
				}
			}
		}

		yield break;
	}

	public void Dispose()
	{
		if (_disposed ) {
			return;
		}

		foreach (var share in _shares.Values) {
			share.Disconnect();
		}

		_shares.Clear();

		foreach (var client in _clients.Values) {
			if (client.IsConnected) {
				client.Logoff();
				client.Disconnect();
			}
		}

		GC.SuppressFinalize(this);
	}

	private SMB2Client GetSmbClient(string server)
	{
		if (_clients.TryGetValue(server, out var smb) && smb.IsConnected) {
			return smb;
		}

		smb ??= new SMB2Client();

		if (!smb.Connect(server, SMBLibrary.SMBTransportType.DirectTCPTransport)) {
			throw new Exception($"Unable to connect to SMB server {server}");
		}

		var shareOpts = _options.Servers.FirstOrDefault(so => so.Server.Equals(server, StringComparison.InvariantCultureIgnoreCase));
		var listat    = smb.Login(shareOpts?.Domain ?? string.Empty, shareOpts?.Username ?? string.Empty, shareOpts?.Password ?? string.Empty);

		if (listat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
			throw new Exception($"Failed to login to SMB server {server} with status {listat}");
		}

		_clients.Add(server, smb);

		return smb;
	}

	private ISMBFileStore GetFileStore(SMB2Client client, string share)
	{
		if (!_shares.TryGetValue(share, out var store)) {
			store = client.TreeConnect(share, out var tcstat);

			if (tcstat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
				throw new Exception($"failed to connect to share with status {tcstat}");
			}

			_shares.Add(share, store);

			_logger.LogInformation("Connected to share {share} on SMB server {server}", share, "(unknown)");
		}

		return store;
	}

	private (string Server, string Share, string Path) ParseUnc(string uncPath)
	{
		if (!uncPath.StartsWith("\\\\")) {
			throw new InvalidOperationException("Invalid UNC path (paths should start with \\\\)");
		}

		var parts = uncPath[2..].Split('\\', 3);

		if (parts.Length < 3) {
			throw new InvalidOperationException("Invalid UNC path");
		}

		return (parts[0], parts[1], parts[2]);
	}
}
