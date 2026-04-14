using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Perelegans.Models;

namespace Perelegans.Data;

public class PerelegansDbContext : DbContext
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<PlaySession> PlaySessions => Set<PlaySession>();

    private readonly string _dbPath;

    public PerelegansDbContext()
    {
        _dbPath = GetDefaultDatabasePath();
    }

    public PerelegansDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    public static string GetDefaultDatabasePath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "perelegans.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Game entity
        modelBuilder.Entity<Game>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Title).IsRequired().HasMaxLength(500);
            entity.Property(g => g.Brand).HasMaxLength(200);
            entity.Property(g => g.ProcessName).HasMaxLength(200);
            entity.Property(g => g.ExecutablePath).HasMaxLength(1000);
            entity.Property(g => g.VndbId).HasMaxLength(50);
            entity.Property(g => g.ErogameSpaceId).HasMaxLength(50);
            entity.Property(g => g.BangumiId).HasMaxLength(50);
            entity.Property(g => g.OfficialWebsite).HasMaxLength(500);
            entity.Property(g => g.Tags);
            entity.Property(g => g.CoverImageUrl).HasMaxLength(1000);
            entity.Property(g => g.CoverImagePath).HasMaxLength(1000);

            // Store TimeSpan as ticks (long)
            entity.Property(g => g.Playtime)
                  .HasConversion(
                      v => v.Ticks,
                      v => TimeSpan.FromTicks(v));

            entity.Property(g => g.Status)
                  .HasConversion<int>();

            entity.HasMany(g => g.PlaySessions)
                  .WithOne(s => s.Game)
                  .HasForeignKey(s => s.GameId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // PlaySession entity
        modelBuilder.Entity<PlaySession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.GameId);

            entity.Property(s => s.Duration)
                  .HasConversion(
                      v => v.Ticks,
                      v => TimeSpan.FromTicks(v));
        });
    }
}
