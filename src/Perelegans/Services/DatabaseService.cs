using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Perelegans.Data;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Provides CRUD operations for the database.
/// </summary>
public class DatabaseService
{
    public string GetDatabasePath() => PerelegansDbContext.GetDefaultDatabasePath();

    /// <summary>
    /// Ensures the database and tables exist.
    /// </summary>
    public async Task EnsureDatabaseCreatedAsync()
    {
        await using var db = new PerelegansDbContext();
        await db.Database.EnsureCreatedAsync();
        await EnsureGamesTagsColumnAsync();
    }

    // ---- Games ----

    public async Task<List<Game>> GetAllGamesAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.Games
            .OrderByDescending(g => g.AccessedDate)
            .ToListAsync();
    }

    public async Task<Game?> GetGameByIdAsync(int id)
    {
        await using var db = new PerelegansDbContext();
        return await db.Games.FindAsync(id);
    }

    public async Task<Game?> GetGameByVndbIdAsync(string vndbId)
    {
        await using var db = new PerelegansDbContext();
        return await db.Games.FirstOrDefaultAsync(g => g.VndbId == vndbId);
    }

    public async Task AddGameAsync(Game game)
    {
        await using var db = new PerelegansDbContext();
        db.Games.Add(game);
        await db.SaveChangesAsync();
    }

    public async Task UpdateGameAsync(Game game)
    {
        await using var db = new PerelegansDbContext();
        db.Games.Update(game);
        await db.SaveChangesAsync();
    }

    public async Task DeleteGameAsync(int gameId)
    {
        await using var db = new PerelegansDbContext();
        var game = await db.Games.FindAsync(gameId);
        if (game != null)
        {
            db.Games.Remove(game);
            await db.SaveChangesAsync();
        }
    }

    // ---- Play Sessions ----

    public async Task AddPlaySessionAsync(PlaySession session)
    {
        await using var db = new PerelegansDbContext();
        db.PlaySessions.Add(session);

        // Also update game's playtime and accessed date
        var game = await db.Games.FindAsync(session.GameId);
        if (game != null)
        {
            game.Playtime += session.Duration;
            game.AccessedDate = session.EndTime;
        }

        await db.SaveChangesAsync();
    }

    public async Task<List<PlaySession>> GetSessionsForGameAsync(int gameId)
    {
        await using var db = new PerelegansDbContext();
        return await db.PlaySessions
            .Where(s => s.GameId == gameId)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
    }

    public async Task<List<PlaySession>> GetAllSessionsAsync()
    {
        await using var db = new PerelegansDbContext();
        return await db.PlaySessions
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
    }

    /// <summary>
    /// Updates a game's playtime and accessed date atomically.
    /// Used by the process monitor during live tracking.
    /// </summary>
    public async Task UpdateGamePlaytimeAsync(int gameId, TimeSpan additionalTime, DateTime accessedDate)
    {
        await using var db = new PerelegansDbContext();
        var game = await db.Games.FindAsync(gameId);
        if (game != null)
        {
            game.Playtime += additionalTime;
            game.AccessedDate = accessedDate;
            await db.SaveChangesAsync();
        }
    }

    public async Task BackupDatabaseAsync(string backupPath)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await using var destination = new SqliteConnection(BuildConnectionString(backupPath));

        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    public async Task RestoreDatabaseAsync(string backupPath)
    {
        SqliteConnection.ClearAllPools();

        await using var source = new SqliteConnection(BuildConnectionString(backupPath));
        await using var destination = new SqliteConnection(BuildConnectionString(GetDatabasePath()));

        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);

        await destination.CloseAsync();
        await source.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static string BuildConnectionString(string dbPath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();
    }

    private async Task EnsureGamesTagsColumnAsync()
    {
        await using var connection = new SqliteConnection(BuildConnectionString(GetDatabasePath()));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Games\");";

        var hasTagsColumn = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "Tags", StringComparison.OrdinalIgnoreCase))
                {
                    hasTagsColumn = true;
                    break;
                }
            }
        }

        if (hasTagsColumn)
            return;

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE \"Games\" ADD COLUMN \"Tags\" TEXT NULL;";
        await alterCommand.ExecuteNonQueryAsync();
    }
}
