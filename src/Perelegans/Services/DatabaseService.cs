using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Perelegans.Data;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Provides CRUD operations for the database.
/// </summary>
public class DatabaseService
{
    /// <summary>
    /// Ensures the database and tables exist.
    /// </summary>
    public async Task EnsureDatabaseCreatedAsync()
    {
        await using var db = new PerelegansDbContext();
        await db.Database.EnsureCreatedAsync();
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
}
