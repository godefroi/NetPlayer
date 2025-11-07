namespace SongServer.Configuration;

public class SmbConnectionOptions
{
	public const string SectionName = "SmbServers";

	public List<ShareOptions> Servers { get; set; } = [];

	public class ShareOptions
	{
		public required string Server { get; set; }
		public string Domain { get; set; } = string.Empty;
		public string Username { get; set; } = string.Empty;
		public string Password { get; set; } = string.Empty;
	}
}
