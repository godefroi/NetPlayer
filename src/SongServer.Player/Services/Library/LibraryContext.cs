using Microsoft.EntityFrameworkCore;

namespace SongServer.Services.Library;

public class LibraryContext : DbContext
{
	public DbSet<Source> Sources { get; set; }

	public DbSet<Song> Songs { get; set; }

	public DbSet<PlaylistFile> Playlist { get; set; }

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
			.ApplyConfigurationsFromAssembly(typeof(LibraryContext).Assembly)
			.UseCollation("NOCASE");
	}
}
