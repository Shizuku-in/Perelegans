using System;
using System.Windows;
using System.Windows.Media;
using ControlzEx.Theming;
using Microsoft.Win32;
using Perelegans.Models;
using Application = System.Windows.Application;

namespace Perelegans.Services;

public class ThemeService
{
    private readonly record struct ThemePalette(
        string ThemeBackground,
        string ThemeForeground,
        string WindowBackground,
        string PanelBackground,
        string StatsChartPanelBackgroundColor,
        string StatsChartPanelBorderColor,
        string StatsChartMetricBackgroundColor,
        string StatsChartSliceBorderColor,
        string StatsChartHighlightBackgroundColor,
        string StatsChartHighlightBorderColor,
        string AccentColor,
        string AccentHoverColor,
        string MahAppsAccentColor,
        string ForegroundColor,
        string SubtleForegroundColor,
        string PlaytimeForegroundColor,
        string StatusBarValueForegroundColor,
        string DataGridSelectedPrimaryForegroundColor,
        string StatusIconColor,
        string CoverStatusBadgeBackgroundColor,
        string CoverStatusBadgeShadowColor,
        string GlassPanelBackgroundColor,
        string GlassPanelBorderColor,
        string CardBackgroundColor,
        string CardShadowColor,
        string BorderColor,
        string ButtonBorderColor,
        string DataGridAltRowColor,
        string DataGridHeaderBackgroundColor,
        string DataGridSelectedRowColor,
        string StatusBarBackgroundColor,
        string MenuBackgroundColor,
        string MenuHoverColor);

    private static readonly ThemePalette LightPalette = new(
        ThemeBackground: "#FDFCF8",
        ThemeForeground: "#1E1E1E",
        WindowBackground: "#F7F3EC",
        PanelBackground: "#F5F3EE",
        StatsChartPanelBackgroundColor: "#FDFCF8",
        StatsChartPanelBorderColor: "#D8D1C6",
        StatsChartMetricBackgroundColor: "#EFECE5",
        StatsChartSliceBorderColor: "#F3EFE8",
        StatsChartHighlightBackgroundColor: "#FFF0F0",
        StatsChartHighlightBorderColor: "#E37F7F",
        AccentColor: "#F48FB1",
        AccentHoverColor: "#F2A3B9",
        MahAppsAccentColor: "#FFC0CB",
        ForegroundColor: "#1E1E1E",
        SubtleForegroundColor: "#706A61",
        PlaytimeForegroundColor: "#FFFFFF",
        StatusBarValueForegroundColor: "#1E1E1E",
        DataGridSelectedPrimaryForegroundColor: "#FFC0CB",
        StatusIconColor: "#D16488",
        CoverStatusBadgeBackgroundColor: "#FDFCF8",
        CoverStatusBadgeShadowColor: "#2D2D30",
        GlassPanelBackgroundColor: "#CCFFFDF8",
        GlassPanelBorderColor: "#80FFFFFF",
        CardBackgroundColor: "#14FFFFFF",
        CardShadowColor: "#26000000",
        BorderColor: "#E0DDD5",
        ButtonBorderColor: "#FFFFFF",
        DataGridAltRowColor: "#F8F6F1",
        DataGridHeaderBackgroundColor: "#EFECE5",
        DataGridSelectedRowColor: "#FFE6ED",
        StatusBarBackgroundColor: "#EFECE5",
        MenuBackgroundColor: "#FDFCF8",
        MenuHoverColor: "#FCE4EC");

    private static readonly ThemePalette DarkPalette = new(
        ThemeBackground: "#252526",
        ThemeForeground: "#DCDCDC",
        WindowBackground: "#161B22",
        PanelBackground: "#222830",
        StatsChartPanelBackgroundColor: "#252526",
        StatsChartPanelBorderColor: "#3A3A3C",
        StatsChartMetricBackgroundColor: "#303033",
        StatsChartSliceBorderColor: "#252526",
        StatsChartHighlightBackgroundColor: "#3A2E31",
        StatsChartHighlightBorderColor: "#E37F7F",
        AccentColor: "#F48FB1",
        AccentHoverColor: "#F7A8C4",
        MahAppsAccentColor: "#FFC0CB",
        ForegroundColor: "#F2F4F8",
        SubtleForegroundColor: "#B0BAC7",
        PlaytimeForegroundColor: "#FFFFFF",
        StatusBarValueForegroundColor: "#E3E7EE",
        DataGridSelectedPrimaryForegroundColor: "#000000",
        StatusIconColor: "#FFD2DB",
        CoverStatusBadgeBackgroundColor: "#222830",
        CoverStatusBadgeShadowColor: "#FDFCF8",
        GlassPanelBackgroundColor: "#99202832",
        GlassPanelBorderColor: "#44FFFFFF",
        CardBackgroundColor: "#18222832",
        CardShadowColor: "#7A000000",
        BorderColor: "#3F3F46",
        ButtonBorderColor: "#3F3F46",
        DataGridAltRowColor: "#2A2A2E",
        DataGridHeaderBackgroundColor: "#333337",
        DataGridSelectedRowColor: "#FFE6ED",
        StatusBarBackgroundColor: "#1E1E1E",
        MenuBackgroundColor: "#252526",
        MenuHoverColor: "#3E3E42");

    private ThemeMode _currentMode = ThemeMode.System;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

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

        string themeKey = isDark ? "Dark.Pink" : "Light.Pink";
        ThemeManager.Current.ChangeTheme(Application.Current, themeKey);
        ApplyCustomOverrides(isDark);
    }

    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyCustomOverrides(bool isDark)
    {
        ApplyPalette(Application.Current.Resources, isDark ? DarkPalette : LightPalette);
    }

    private static void ApplyPalette(ResourceDictionary resources, ThemePalette palette)
    {
        var accentColor = ParseColor(palette.AccentColor);

        resources["MahApps.Brushes.ThemeBackground"] = CreateBrush(palette.ThemeBackground);
        resources["MahApps.Brushes.ThemeForeground"] = CreateBrush(palette.ThemeForeground);
        resources["MahApps.Brushes.Accent"] = CreateBrush(palette.MahAppsAccentColor);

        resources["Perelegans.AccentColor"] = accentColor;
        resources["Perelegans.AccentBrush"] = CreateBrush(palette.AccentColor);
        resources["Perelegans.AccentHoverBrush"] = CreateBrush(palette.AccentHoverColor);
        resources["Perelegans.WindowBackground"] = CreateBrush(palette.WindowBackground);
        resources["Perelegans.PanelBackground"] = CreateBrush(palette.PanelBackground);
        resources["Perelegans.StatsChartPanelBackground"] = CreateBrush(palette.StatsChartPanelBackgroundColor);
        resources["Perelegans.StatsChartPanelBorderBrush"] = CreateBrush(palette.StatsChartPanelBorderColor);
        resources["Perelegans.StatsChartMetricBackground"] = CreateBrush(palette.StatsChartMetricBackgroundColor);
        resources["Perelegans.StatsChartSliceBorderBrush"] = CreateBrush(palette.StatsChartSliceBorderColor);
        resources["Perelegans.StatsChartHighlightBackground"] = CreateBrush(palette.StatsChartHighlightBackgroundColor);
        resources["Perelegans.StatsChartHighlightBorderBrush"] = CreateBrush(palette.StatsChartHighlightBorderColor);
        resources["Perelegans.ForegroundBrush"] = CreateBrush(palette.ForegroundColor);
        resources["Perelegans.SubtleForegroundBrush"] = CreateBrush(palette.SubtleForegroundColor);
        resources["Perelegans.PlaytimeForegroundBrush"] = CreateBrush(palette.PlaytimeForegroundColor);
        resources["Perelegans.StatusBarValueForegroundBrush"] = CreateBrush(palette.StatusBarValueForegroundColor);
        resources["Perelegans.DataGridSelectedPrimaryForegroundBrush"] = CreateBrush(palette.DataGridSelectedPrimaryForegroundColor);
        resources["Perelegans.StatusIconBrush"] = CreateBrush(palette.StatusIconColor);
        resources["Perelegans.CoverStatusBadgeBackgroundBrush"] = CreateBrush(palette.CoverStatusBadgeBackgroundColor);
        resources["Perelegans.CoverStatusBadgeShadowColor"] = ParseColor(palette.CoverStatusBadgeShadowColor);
        resources["Perelegans.GlassPanelBackground"] = CreateBrush(palette.GlassPanelBackgroundColor);
        resources["Perelegans.GlassPanelBorderBrush"] = CreateBrush(palette.GlassPanelBorderColor);
        resources["Perelegans.CardBackgroundBrush"] = CreateBrush(palette.CardBackgroundColor);
        resources["Perelegans.CardShadowColor"] = ParseColor(palette.CardShadowColor);
        resources["Perelegans.BorderBrush"] = CreateBrush(palette.BorderColor);
        resources["Perelegans.ButtonBorderBrush"] = CreateBrush(palette.ButtonBorderColor);
        resources["Perelegans.DataGridAltRowBrush"] = CreateBrush(palette.DataGridAltRowColor);
        resources["Perelegans.DataGridHeaderBackground"] = CreateBrush(palette.DataGridHeaderBackgroundColor);
        resources["Perelegans.DataGridSelectedRowBrush"] = CreateBrush(palette.DataGridSelectedRowColor);
        resources["Perelegans.StatusBarBackground"] = CreateBrush(palette.StatusBarBackgroundColor);
        resources["Perelegans.MenuBackground"] = CreateBrush(palette.MenuBackgroundColor);
        resources["Perelegans.MenuHoverBrush"] = CreateBrush(palette.MenuHoverColor);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Color ParseColor(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
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


