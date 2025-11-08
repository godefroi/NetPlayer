using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Extensions;
using SMBLibrary.Client;
using SongServer.Services.Smb;

namespace SongServer.Services.Library;

public class FileInformationRetriever(SmbFileClient smbClient)
{
	private const string _smbScheme = "smb";

	private readonly SmbFileClient _smbClient = smbClient;

	public IEnumerable<Uri> EnumerateFiles(Uri sourceUri)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && sourceUri.Scheme == _smbScheme) {
			var (server, share, path) = ParseUri(sourceUri);

			return _smbClient.EnumerateFiles(server, share, path)
				.Select(p => {
					var fullPath = string.Concat(share, '/', path, '/', p.Replace(Path.DirectorySeparatorChar, '/'));
					return new UriBuilder(_smbScheme, server) { Path = fullPath }.Uri;
				});
		} else if (sourceUri.Scheme == _smbScheme) {
			// smb scheme, but we're on Windows... we can treat it like we would a file://
			// and then just turn them back into smb:// urls
			return EnumerateFiles(new UriBuilder(sourceUri) { Scheme = Uri.UriSchemeFile }.Uri)
				.Select(u => new UriBuilder(u) { Scheme = _smbScheme }.Uri);
		} else if (sourceUri.Scheme == Uri.UriSchemeFile) {
			return Directory
				.EnumerateFiles(sourceUri.LocalPath, "*.*", SearchOption.AllDirectories)
				.Select(f => new Uri(Path.Combine(sourceUri.LocalPath, f), UriKind.Absolute));
		} else {
			throw new NotSupportedException($"URI scheme {sourceUri.Scheme} is not supported.");
		}
	}

	public async Task<FileRecord> ReadFileInfo(Uri fileUri)
	{
		// if it's smb:// but we're on windows, we can just pretend it was file://
		if (fileUri.Scheme == _smbScheme && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			return await ReadFileInfo(new UriBuilder(fileUri) { Scheme = Uri.UriSchemeFile }.Uri);
		}

		using var sha256 = SHA256.Create();

		if (fileUri.Scheme == _smbScheme) {
			var (server, share, path) = ParseUri(fileUri);

			// read the file via SMB
			TagLib.Tag tag;
			TimeSpan duration;
			byte[] hash;
			DateTime lastWrite;
			long size;

			var (store, handle) = _smbClient.OpenRead(server, share, path);
			try {
				(size, lastWrite) = GetFileInformation(store, handle);
				using var tagFile = TagLib.File.Create(new SmbFileAbstraction(store, handle, Path.GetFileName(fileUri.LocalPath)));
				tag = tagFile.Tag;
				duration = tagFile.Properties.Duration;
			} finally {
				store.CloseFile(handle);
			}

			using (var stream = _smbClient.OpenStream(server, share, path)) {
				hash = await sha256.ComputeHashAsync(stream);
			}

			return new(Convert.ToHexStringLower(hash), tag, duration, lastWrite, size);
		} else {
			var fInfo = new FileInfo(fileUri.LocalPath);
			using var stream = File.OpenRead(fileUri.LocalPath);
			var hashTask = sha256.ComputeHashAsync(stream);
			using var tagFile = TagLib.File.Create(fileUri.LocalPath);

			return new(Convert.ToHexStringLower(await hashTask), tagFile.Tag, tagFile.Properties.Duration, fInfo.LastWriteTime, fInfo.Length);
		}
	}

	public Stream OpenFileStream(Uri fileUri)
	{
		if (fileUri.Scheme == _smbScheme && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			return OpenFileStream(new UriBuilder(fileUri) { Scheme = Uri.UriSchemeFile }.Uri);
		}

		if (fileUri.Scheme == _smbScheme) {
			var (server, share, path) = ParseUri(fileUri);
			return _smbClient.OpenStream(server, share, path);
		} else {
			return File.OpenRead(fileUri.LocalPath);
		}
	}

	private static (long Size, DateTime LastWriteTime) GetFileInformation(ISMBFileStore store, object handle)
	{
		var fistat = store.GetFileInformation(out var info, handle, SMBLibrary.FileInformationClass.FileAllInformation);

		if (fistat != SMBLibrary.NTStatus.STATUS_SUCCESS) {
			throw new IOException($"Failed to query file information: {fistat}");
		}

		if (info is not SMBLibrary.FileAllInformation fsi) {
			throw new IOException("Wrong file information class returned from GetFileInformation");
		}

		return (fsi.StandardInformation.EndOfFile, fsi.BasicInformation.LastWriteTime.Time ?? DateTime.MinValue);
	}

	private static (string Server, string Share, string Path) ParseUri(Uri uri)
	{
		if (uri.Scheme != _smbScheme) {
			throw new InvalidOperationException("Can only parse smb:// URIs");
		}

		var server = uri.Host;
		var share  = uri.Segments[1].TrimEnd('/');
		var path   = string.Concat(uri.Segments[2..]).TrimEnd('/');

		return (Uri.UnescapeDataString(server), Uri.UnescapeDataString(share), Uri.UnescapeDataString(path));
	}
}

public record FileRecord(string Hash, TagLib.Tag Tag, TimeSpan Duration, DateTime LastWrite, long Length);
