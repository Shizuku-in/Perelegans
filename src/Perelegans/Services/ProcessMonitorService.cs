using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Background service that monitors running processes and tracks game playtime.
/// </summary>
public class ProcessMonitorService
{
    private readonly DatabaseService _dbService;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, ActiveSession> _activeSessions = new();

    /// <summary>
    /// List of games to monitor (process names mapped to game IDs).
    /// </summary>
    private List<Game> _monitoredGames = new();

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Fired when a game's playtime is updated so the UI can refresh.
    /// </summary>
    public event Action<int, TimeSpan>? PlaytimeUpdated;

    /// <summary>
    /// Fired when a game starts or stops being detected.
    /// </summary>
    public event Action<int, bool>? GameDetectionChanged;

    public ProcessMonitorService(DatabaseService dbService)
    {
        _dbService = dbService;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Sets the monitoring interval.
    /// </summary>
    public void SetInterval(int seconds)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    /// <summary>
    /// Updates the list of games to monitor.
    /// </summary>
    public void UpdateMonitoredGames(IEnumerable<Game> games)
    {
        _monitoredGames = games
            .Where(g => !string.IsNullOrWhiteSpace(g.ProcessName))
            .ToList();
    }

    /// <summary>
    /// Starts the process monitoring timer.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _timer.Start();
    }

    /// <summary>
    /// Stops the process monitoring timer and finalizes active sessions.
    /// </summary>
    public void Stop()
    {
        _ = StopAsync();
    }

    /// <summary>
    /// Stops the process monitoring timer and finalizes active sessions.
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _timer.Stop();
        IsRunning = false;

        // Finalize all active sessions
        foreach (var kvp in _activeSessions.ToList())
        {
            await FinalizeSession(kvp.Key, kvp.Value);
            GameDetectionChanged?.Invoke(kvp.Key, false);
        }
        _activeSessions.Clear();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            var runningProcessNames = Process.GetProcesses()
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return null; }
                })
                .Where(n => n != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check each monitored game
            foreach (var game in _monitoredGames)
            {
                bool isRunning = runningProcessNames.Contains(game.ProcessName);

                if (isRunning && !_activeSessions.ContainsKey(game.Id))
                {
                    // Game just started
                    _activeSessions[game.Id] = new ActiveSession
                    {
                        GameId = game.Id,
                        StartTime = DateTime.Now,
                        LastTick = DateTime.Now
                    };
                    GameDetectionChanged?.Invoke(game.Id, true);
                }
                else if (isRunning && _activeSessions.ContainsKey(game.Id))
                {
                    // Game still running - update elapsed time
                    var session = _activeSessions[game.Id];
                    var now = DateTime.Now;
                    var elapsed = now - session.LastTick;
                    session.LastTick = now;
                    session.AccumulatedTime += elapsed;

                    // Notify UI of updated playtime
                    PlaytimeUpdated?.Invoke(game.Id, elapsed);
                }
                else if (!isRunning && _activeSessions.TryGetValue(game.Id, out var session))
                {
                    // Game just stopped
                    await FinalizeSession(game.Id, session);
                    _activeSessions.Remove(game.Id);
                    GameDetectionChanged?.Invoke(game.Id, false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ProcessMonitor error: {ex.Message}");
        }
    }

    private async Task FinalizeSession(int gameId, ActiveSession session)
    {
        if (session.AccumulatedTime.TotalSeconds < 1) return;

        var playSession = new PlaySession
        {
            GameId = gameId,
            StartTime = session.StartTime,
            EndTime = DateTime.Now,
            Duration = session.AccumulatedTime
        };

        try
        {
            await _dbService.AddPlaySessionAsync(playSession);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save play session: {ex.Message}");
        }
    }

    private class ActiveSession
    {
        public int GameId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastTick { get; set; }
        public TimeSpan AccumulatedTime { get; set; } = TimeSpan.Zero;
    }
}
