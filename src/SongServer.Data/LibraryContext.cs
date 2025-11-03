using Microsoft.Data.Sqlite;

namespace SongServer.Data;

public class LibraryContext : DbContext
{
	public DbSet<Source> Sources { get; set; }

	public DbSet<Song> Songs { get; set; }

	public DbSet<PlaylistFile> Playlist { get; set; }

	public DbSet<StreamMetadata> StreamMetadata { get; set; }

	public DbSet<State> State { get; set; }

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		if (!optionsBuilder.IsConfigured) {
			optionsBuilder
				.UseSqlite("Data Source=music_library.db")
				.EnableSensitiveDataLogging();
		}
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder
			.ApplyConfigurationsFromAssembly(typeof(LibraryContext).Assembly);
	}

	// public void Backup()
	// {
	// 	var conn = Database.GetDbConnection() as SqliteConnection;
	// 	conn.BackupDatabase()
	// }
}
