using Microsoft.EntityFrameworkCore;

namespace SongClassifier.Billboard;

public class BillboardContext : DbContext
{
	public DbSet<RadioSong> Radio { get; set; }

	public DbSet<Hot100Song> Hot100 { get; set; }

	public DbSet<SongFile> Files { get; set; }

	public DbSet<PlaylistFile> Playlist { get; set; }

	public DbSet<SongRank> SongRanks { get; set; }

	public DbSet<RankedFile> RankedFiles { get; set; }

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		if (!optionsBuilder.IsConfigured) {
			optionsBuilder
				.UseSqlite("Data Source=billboard_data.db")
				.EnableSensitiveDataLogging();
		}
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<Song>(entity => {
			entity.UseTpcMappingStrategy();

			entity.HasKey(e => new { e.Date, e.Title, e.Artist });

			entity.Property(e => e.Date)
				.HasColumnType("TEXT")
				.IsRequired();

			entity.Property(e => e.Title)
				.HasColumnType("TEXT")
				.IsRequired();

			entity.Property(e => e.Artist)
				.HasColumnType("TEXT")
				.IsRequired();

			entity.Property(e => e.Rank)
				.HasColumnType("INTEGER")
				.IsRequired();

			entity.Property(e => e.LastWeek)
				.HasColumnType("INTEGER");

			entity.Property(e => e.PeakPos)
				.HasColumnType("INTEGER")
				.IsRequired();

			entity.Property(e => e.WeeksOnChart)
				.HasColumnType("INTEGER");

			entity.Property(e => e.ImageUrl)
				.HasColumnType("TEXT");
		});

		modelBuilder.Entity<RadioSong>().ToTable("radio");
		modelBuilder.Entity<Hot100Song>().ToTable("hot_hundred");
		modelBuilder.Entity<SongRank>().ToView("song_ranks").HasNoKey();
		modelBuilder.Entity<SongFile>().ToTable("files").HasKey(e => new { e.Artist, e.Album, e.Title });
		modelBuilder.Entity<RankedFile>().ToView("ranked_files").HasNoKey();

		modelBuilder.Entity<PlaylistFile>(entity =>
			entity.HasKey(e => e.Filename));
	}
}
