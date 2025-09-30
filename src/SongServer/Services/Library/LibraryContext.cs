using Microsoft.EntityFrameworkCore;

namespace SongServer.Services.Library;

public class BillboardContext : DbContext
{
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
	}
}
