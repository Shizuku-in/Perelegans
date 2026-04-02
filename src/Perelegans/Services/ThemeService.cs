using System;
using System.Windows;
using ControlzEx.Theming;
using Microsoft.Win32;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Manages application theming: detects Windows system theme and applies
/// custom Light (off-white + pink) or Dark (gray + pink) themes.
/// </summary>
public class ThemeService
{
    private ThemeMode _currentMode = ThemeMode.System;

    public ThemeService()
    {
        // Listen for Windows theme changes
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

    /// <summary>
    /// Applies the specified theme mode.
    /// </summary>
    public void ApplyTheme(ThemeMode mode)
    {
        _currentMode = mode;

        bool isDark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            ThemeMode.System => IsSystemDarkMode(),
            _ => false
        };

        string baseTheme = isDark ? "Dark" : "Light";
        string themeKey = isDark ? "Dark.Pink" : "Light.Pink";

        // Apply MahApps base theme with Pink accent
        ThemeManager.Current.ChangeTheme(Application.Current, themeKey);

        // Apply our custom overrides
        ApplyCustomOverrides(isDark);
    }

    /// <summary>
    /// Detects whether Windows is currently in dark mode.
    /// </summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            // 0 = dark mode, 1 = light mode
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies custom color overrides (off-white/gray backgrounds, pink accents).
    /// </summary>
    private void ApplyCustomOverrides(bool isDark)
    {
        var app = Application.Current;
        var resources = app.Resources;

        if (isDark)
        {
            // Dark theme: gray backgrounds + adjusted pink accent
            resources["MahApps.Brushes.ThemeBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526"));
            resources["MahApps.Brushes.ThemeForeground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DCDCDC"));

            // Custom accent brushes for dark mode (slightly desaturated pink)
            var darkPink = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F06292");
            resources["Perelegans.AccentColor"] = darkPink;
            resources["Perelegans.AccentBrush"] = new System.Windows.Media.SolidColorBrush(darkPink);
            resources["Perelegans.WindowBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526"));
            resources["Perelegans.PanelBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D30"));
            resources["Perelegans.ForegroundBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DCDCDC"));
            resources["Perelegans.SubtleForegroundBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9E9E9E"));
            resources["Perelegans.BorderBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3F3F46"));
            resources["Perelegans.DataGridAltRowBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2A2A2E"));
        }
        else
        {
            // Light theme: off-white backgrounds + bright pink accent
            resources["MahApps.Brushes.ThemeBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDFCF8"));
            resources["MahApps.Brushes.ThemeForeground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));

            var lightPink = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E91E78");
            resources["Perelegans.AccentColor"] = lightPink;
            resources["Perelegans.AccentBrush"] = new System.Windows.Media.SolidColorBrush(lightPink);
            resources["Perelegans.WindowBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDFCF8"));
            resources["Perelegans.PanelBackground"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F5F3EE"));
            resources["Perelegans.ForegroundBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
            resources["Perelegans.SubtleForegroundBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#757575"));
            resources["Perelegans.BorderBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0DDD5"));
            resources["Perelegans.DataGridAltRowBrush"] =
                new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F8F6F1"));
        }
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _currentMode == ThemeMode.System)
        {
            Application.Current.Dispatcher.Invoke(() => ApplyTheme(ThemeMode.System));
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
    }
}
