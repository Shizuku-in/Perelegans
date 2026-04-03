using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly SettingsService _settingsService;
    private readonly StartupRegistrationService _startupRegistrationService;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private int _monitorIntervalSeconds;

    [ObservableProperty]
    private string _proxyAddress = string.Empty;

    [ObservableProperty]
    private bool _monitorEnabled;

    [ObservableProperty]
    private string _selectedLanguage = "zh-Hans";

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private AppCloseBehavior _selectedCloseBehavior;

    public string[] LanguageOptions { get; } = ["zh-Hans", "en-US", "ja-JP"];
    public IReadOnlyList<AppCloseBehaviorOption> CloseBehaviorOptions { get; } =
    [
        new(AppCloseBehavior.Exit, TranslationService.Instance["Settings_CloseBehaviorExit"]),
        new(AppCloseBehavior.MinimizeToTray, TranslationService.Instance["Settings_CloseBehaviorTray"])
    ];

    public SettingsViewModel(
        ThemeService themeService,
        SettingsService settingsService,
        StartupRegistrationService startupRegistrationService)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _startupRegistrationService = startupRegistrationService;

        // Load current settings
        var s = settingsService.Settings;
        _selectedTheme = s.Theme;
        _monitorIntervalSeconds = s.MonitorIntervalSeconds;
        _proxyAddress = s.ProxyAddress;
        _monitorEnabled = s.MonitorEnabled;
        _selectedLanguage = TranslationService.NormalizeLanguageCode(s.Language);
        _launchAtStartup = s.LaunchAtStartup;
        _selectedCloseBehavior = s.CloseBehavior;
    }

    /// <summary>
    /// Saves settings and applies changes.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        _startupRegistrationService.SetEnabled(LaunchAtStartup);

        var s = _settingsService.Settings;
        s.Theme = SelectedTheme;
        s.MonitorIntervalSeconds = MonitorIntervalSeconds;
        s.ProxyAddress = ProxyAddress;
        s.MonitorEnabled = MonitorEnabled;
        s.Language = TranslationService.NormalizeLanguageCode(SelectedLanguage);
        s.LaunchAtStartup = LaunchAtStartup;
        s.CloseBehavior = SelectedCloseBehavior;

        _settingsService.Save();
        _themeService.ApplyTheme(SelectedTheme);
        TranslationService.Instance.ChangeLanguage(s.Language);
    }

    /// <summary>
    /// Whether settings have been modified from saved values.
    /// </summary>
    public bool HasChanges
    {
        get
        {
            var s = _settingsService.Settings;
            return s.Theme != SelectedTheme
                || s.MonitorIntervalSeconds != MonitorIntervalSeconds
                || s.ProxyAddress != ProxyAddress
                || s.MonitorEnabled != MonitorEnabled
                || s.Language != SelectedLanguage
                || s.LaunchAtStartup != LaunchAtStartup
                || s.CloseBehavior != SelectedCloseBehavior;
        }
    }
}

public sealed class AppCloseBehaviorOption(AppCloseBehavior value, string label)
{
    public AppCloseBehavior Value { get; } = value;
    public string Label { get; } = label;
}
