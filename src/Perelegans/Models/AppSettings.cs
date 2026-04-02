using System.Text.Json.Serialization;

namespace Perelegans.Models;

public enum AppCloseBehavior
{
    Exit = 0,
    MinimizeToTray = 1
}

/// <summary>
/// Application settings persisted as JSON.
/// </summary>
public class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Process monitoring interval in seconds.
    /// </summary>
    public int MonitorIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// HTTP proxy address (e.g. "http://127.0.0.1:7890").
    /// </summary>
    public string ProxyAddress { get; set; } = string.Empty;

    /// <summary>
    /// Whether process monitoring is enabled on startup.
    /// </summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>
    /// UI Language code (e.g. zh-Hans, en-US, ja-JP).
    /// </summary>
    public string Language { get; set; } = "zh-Hans";

    /// <summary>
    /// Whether the app should register itself to launch at Windows sign-in.
    /// </summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// Behavior to apply when the main window is closed.
    /// </summary>
    public AppCloseBehavior CloseBehavior { get; set; } = AppCloseBehavior.Exit;
}
