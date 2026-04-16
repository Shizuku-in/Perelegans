using System.Text.Json.Serialization;

namespace Perelegans.Models;

public enum AppCloseBehavior
{
    Exit = 0,
    MinimizeToTray = 1
}

public enum AiProvider
{
    Auto = 0,
    OpenAI = 1,
    OpenRouter = 2,
    Anthropic = 3
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

    /// <summary>
    /// AI provider protocol.
    /// </summary>
    public AiProvider AiProvider { get; set; } = AiProvider.Auto;

    /// <summary>
    /// AI API base URL (e.g. https://api.openai.com/v1).
    /// </summary>
    public string AiApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// AI API key.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier for recommendation explanations.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;
}
