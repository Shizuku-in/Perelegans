using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private int _monitorIntervalSeconds;

    [ObservableProperty]
    private string _proxyAddress = string.Empty;

    [ObservableProperty]
    private bool _monitorEnabled;

    public SettingsViewModel(ThemeService themeService, SettingsService settingsService)
    {
        _themeService = themeService;
        _settingsService = settingsService;

        // Load current settings
        var s = settingsService.Settings;
        _selectedTheme = s.Theme;
        _monitorIntervalSeconds = s.MonitorIntervalSeconds;
        _proxyAddress = s.ProxyAddress;
        _monitorEnabled = s.MonitorEnabled;
    }

    /// <summary>
    /// Saves settings and applies changes.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Settings;
        s.Theme = SelectedTheme;
        s.MonitorIntervalSeconds = MonitorIntervalSeconds;
        s.ProxyAddress = ProxyAddress;
        s.MonitorEnabled = MonitorEnabled;

        _settingsService.Save();
        _themeService.ApplyTheme(SelectedTheme);
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
                || s.MonitorEnabled != MonitorEnabled;
        }
    }
}
