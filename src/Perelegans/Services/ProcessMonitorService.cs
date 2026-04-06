using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// List of games to monitor, primarily matched by executable path.
    /// </summary>
    private List<MonitoredGame> _monitoredGames = new();

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
            .Select(g => new MonitoredGame
            {
                GameId = g.Id,
                NormalizedExecutablePath = NormalizeExecutablePath(g.ExecutablePath),
                NormalizedProcessName = NormalizeProcessName(g.ProcessName)
            })
            .Where(g => !string.IsNullOrWhiteSpace(g.NormalizedExecutablePath) ||
                        !string.IsNullOrWhiteSpace(g.NormalizedProcessName))
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
            var runningProcesses = CaptureRunningProcesses();

            // Check each monitored game
            foreach (var game in _monitoredGames)
            {
                bool isRunning = !string.IsNullOrWhiteSpace(game.NormalizedExecutablePath)
                    ? runningProcesses.ExecutablePaths.Contains(game.NormalizedExecutablePath)
                    : runningProcesses.ProcessNames.Contains(game.NormalizedProcessName);

                if (isRunning && !_activeSessions.ContainsKey(game.GameId))
                {
                    // Game just started
                    _activeSessions[game.GameId] = new ActiveSession
                    {
                        GameId = game.GameId,
                        StartTime = DateTime.Now,
                        LastTick = DateTime.Now
                    };
                    GameDetectionChanged?.Invoke(game.GameId, true);
                }
                else if (isRunning && _activeSessions.ContainsKey(game.GameId))
                {
                    // Game still running - update elapsed time
                    var session = _activeSessions[game.GameId];
                    var now = DateTime.Now;
                    var elapsed = now - session.LastTick;
                    session.LastTick = now;
                    session.AccumulatedTime += elapsed;

                    // Notify UI of updated playtime
                    PlaytimeUpdated?.Invoke(game.GameId, elapsed);
                }
                else if (!isRunning && _activeSessions.TryGetValue(game.GameId, out var session))
                {
                    // Game just stopped
                    await FinalizeSession(game.GameId, session);
                    _activeSessions.Remove(game.GameId);
                    GameDetectionChanged?.Invoke(game.GameId, false);
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

    private static RunningProcessSnapshot CaptureRunningProcesses()
    {
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var normalizedProcessName = NormalizeProcessName(TryGetProcessName(process));
                if (!string.IsNullOrWhiteSpace(normalizedProcessName))
                    processNames.Add(normalizedProcessName);

                var normalizedExecutablePath = NormalizeExecutablePath(TryGetExecutablePath(process));
                if (!string.IsNullOrWhiteSpace(normalizedExecutablePath))
                    executablePaths.Add(normalizedExecutablePath);
            }
            finally
            {
                process.Dispose();
            }
        }

        return new RunningProcessSnapshot(processNames, executablePaths);
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string? processName)
    {
        return string.IsNullOrWhiteSpace(processName)
            ? string.Empty
            : processName.Trim();
    }

    private static string NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return string.Empty;

        var trimmed = executablePath.Trim().Trim('"');

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private sealed record RunningProcessSnapshot(
        HashSet<string> ProcessNames,
        HashSet<string> ExecutablePaths);

    private sealed class MonitoredGame
    {
        public int GameId { get; set; }
        public string NormalizedExecutablePath { get; set; } = string.Empty;
        public string NormalizedProcessName { get; set; } = string.Empty;
    }
}
