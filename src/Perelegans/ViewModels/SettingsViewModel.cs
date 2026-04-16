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

    [ObservableProperty]
    private AiProvider _selectedAiProvider;

    [ObservableProperty]
    private string _aiApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    [ObservableProperty]
    private string _aiModel = string.Empty;

    public string[] LanguageOptions { get; } = ["zh-Hans", "en-US", "ja-JP"];
    public IReadOnlyList<AppCloseBehaviorOption> CloseBehaviorOptions { get; } =
    [
        new(AppCloseBehavior.Exit, TranslationService.Instance["Settings_CloseBehaviorExit"]),
        new(AppCloseBehavior.MinimizeToTray, TranslationService.Instance["Settings_CloseBehaviorTray"])
    ];

    public IReadOnlyList<AiProviderOption> AiProviderOptions { get; } =
    [
        new(AiProvider.Auto, TranslationService.Instance["Settings_AiProvider_Auto"]),
        new(AiProvider.OpenAI, TranslationService.Instance["Settings_AiProvider_OpenAI"]),
        new(AiProvider.OpenRouter, TranslationService.Instance["Settings_AiProvider_OpenRouter"]),
        new(AiProvider.Anthropic, TranslationService.Instance["Settings_AiProvider_Anthropic"])
    ];

    public string AiBaseUrlPlaceholder => SelectedAiProvider switch
    {
        AiProvider.Anthropic => "https://api.anthropic.com",
        AiProvider.OpenRouter => "https://openrouter.ai/api/v1",
        _ => "https://api.openai.com/v1"
    };

    public string AiApiKeyPlaceholder => SelectedAiProvider switch
    {
        AiProvider.Anthropic => "sk-ant-...",
        _ => "sk-..."
    };

    public string AiModelPlaceholder => SelectedAiProvider switch
    {
        AiProvider.Anthropic => "claude-3-5-sonnet-latest",
        AiProvider.OpenRouter => "anthropic/claude-3.5-sonnet",
        _ => "gpt-4.1-mini"
    };

    public string AiProviderHint => SelectedAiProvider switch
    {
        AiProvider.Anthropic => TranslationService.Instance["Settings_AiProviderHint_Anthropic"],
        AiProvider.OpenRouter => TranslationService.Instance["Settings_AiProviderHint_OpenRouter"],
        AiProvider.OpenAI => TranslationService.Instance["Settings_AiProviderHint_OpenAI"],
        _ => TranslationService.Instance["Settings_AiProviderHint_Auto"]
    };

    public SettingsViewModel(
        ThemeService themeService,
        SettingsService settingsService,
        StartupRegistrationService startupRegistrationService)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _startupRegistrationService = startupRegistrationService;

        var s = settingsService.Settings;
        _selectedTheme = s.Theme;
        _monitorIntervalSeconds = s.MonitorIntervalSeconds;
        _proxyAddress = s.ProxyAddress;
        _monitorEnabled = s.MonitorEnabled;
        _selectedLanguage = TranslationService.NormalizeLanguageCode(s.Language);
        _launchAtStartup = s.LaunchAtStartup;
        _selectedCloseBehavior = s.CloseBehavior;
        _selectedAiProvider = s.AiProvider;
        _aiApiBaseUrl = s.AiApiBaseUrl;
        _aiApiKey = s.AiApiKey;
        _aiModel = s.AiModel;
    }

    partial void OnSelectedAiProviderChanged(AiProvider value)
    {
        OnPropertyChanged(nameof(AiBaseUrlPlaceholder));
        OnPropertyChanged(nameof(AiApiKeyPlaceholder));
        OnPropertyChanged(nameof(AiModelPlaceholder));
        OnPropertyChanged(nameof(AiProviderHint));
    }

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
        s.AiProvider = SelectedAiProvider;
        s.AiApiBaseUrl = AiApiBaseUrl.Trim();
        s.AiApiKey = AiApiKey.Trim();
        s.AiModel = AiModel.Trim();

        _settingsService.Save();
        _themeService.ApplyTheme(SelectedTheme);
        TranslationService.Instance.ChangeLanguage(s.Language);
    }

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
                || s.CloseBehavior != SelectedCloseBehavior
                || s.AiProvider != SelectedAiProvider
                || s.AiApiBaseUrl != AiApiBaseUrl
                || s.AiApiKey != AiApiKey
                || s.AiModel != AiModel;
        }
    }
}

public sealed class AppCloseBehaviorOption(AppCloseBehavior value, string label)
{
    public AppCloseBehavior Value { get; } = value;
    public string Label { get; } = label;
}

public sealed class AiProviderOption(AiProvider value, string label)
{
    public AiProvider Value { get; } = value;
    public string Label { get; } = label;
}
