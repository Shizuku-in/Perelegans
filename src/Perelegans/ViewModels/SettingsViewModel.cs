using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
    private const string AnthropicVersion = "2023-06-01";

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

    [ObservableProperty]
    private string _aiTestStatusText = string.Empty;

    [ObservableProperty]
    private bool _isTestingAi;

    public bool HasAiTestStatus => !string.IsNullOrWhiteSpace(AiTestStatusText);

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

    partial void OnAiTestStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAiTestStatus));
    }

    [RelayCommand]
    private async Task TestAiAsync()
    {
        if (IsTestingAi)
            return;

        AiTestStatusText = string.Empty;
        if (string.IsNullOrWhiteSpace(AiApiBaseUrl) ||
            string.IsNullOrWhiteSpace(AiApiKey) ||
            string.IsNullOrWhiteSpace(AiModel))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestMissingConfig"];
            return;
        }

        if (!Uri.TryCreate(AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            AiTestStatusText = TranslationService.Instance["Settings_AiTestInvalidUrl"];
            return;
        }

        IsTestingAi = true;
        AiTestStatusText = TranslationService.Instance["Settings_AiTestRunning"];

        try
        {
            using var httpClient = MetadataHttpClientFactory.Create(new AppSettings
            {
                ProxyAddress = ProxyAddress
            });

            var provider = ResolveAiProvider(baseUri);
            using var request = BuildAiTestRequest(provider, baseUri);
            using var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                AiTestStatusText = string.Format(
                    TranslationService.Instance["Settings_AiTestFailed"],
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {TruncateForDisplay(responseBody)}");
                return;
            }

            var content = ExtractAiTestContent(responseBody, provider);
            if (string.IsNullOrWhiteSpace(content))
            {
                AiTestStatusText = string.Format(
                    TranslationService.Instance["Settings_AiTestFailed"],
                    $"{TranslationService.Instance["Settings_AiTestNoContent"]} Raw: {TruncateForDisplay(responseBody)}");
                return;
            }

            AiTestStatusText = string.Format(
                TranslationService.Instance["Settings_AiTestSuccess"],
                TruncateForDisplay(content));
        }
        catch (System.Exception ex)
        {
            AiTestStatusText = string.Format(
                TranslationService.Instance["Settings_AiTestFailed"],
                ex.Message);
        }
        finally
        {
            IsTestingAi = false;
        }
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

    private AiProvider ResolveAiProvider(Uri baseUri)
    {
        if (SelectedAiProvider != AiProvider.Auto)
            return SelectedAiProvider;

        var host = baseUri.Host.ToLowerInvariant();
        if (host.Contains("anthropic"))
            return AiProvider.Anthropic;
        if (host.Contains("openrouter"))
            return AiProvider.OpenRouter;

        return AiProvider.OpenAI;
    }

    private HttpRequestMessage BuildAiTestRequest(AiProvider provider, Uri baseUri)
    {
        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", System.StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
        var endpoint = BuildAiEndpoint(normalizedBase, provider);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("User-Agent", "Perelegans/0.2");

        if (provider == AiProvider.Anthropic)
        {
            request.Headers.Add("x-api-key", AiApiKey.Trim());
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = AiModel.Trim(),
                    max_tokens = 32,
                    temperature = 0,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = "Reply exactly: ok"
                        }
                    }
                }),
                Encoding.UTF8,
                "application/json");
            return request;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiApiKey.Trim());
        if (provider == AiProvider.OpenRouter)
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/Shizuku-in/Perelegans");
            request.Headers.Add("X-Title", "Perelegans");
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = AiModel.Trim(),
                temperature = 0,
                max_tokens = 32,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = "Reply exactly: ok"
                    }
                }
            }),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static Uri BuildAiEndpoint(Uri normalizedBase, AiProvider provider)
    {
        if (provider == AiProvider.Anthropic)
        {
            return normalizedBase.AbsolutePath.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
                ? normalizedBase
                : new Uri(normalizedBase, "v1/messages");
        }

        return normalizedBase.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalizedBase
            : new Uri(normalizedBase, "chat/completions");
    }

    private static string ExtractAiTestContent(string responseJson, AiProvider provider)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (provider == AiProvider.Anthropic)
        {
            if (document.RootElement.TryGetProperty("content", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                return ExtractTextFromContentArray(contentArray);
            }

            return string.Empty;
        }

        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            TryExtractMessageText(message, out var messageText))
        {
            return messageText;
        }

        if (document.RootElement.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentArray) &&
                    contentArray.ValueKind == JsonValueKind.Array)
                {
                    var text = ExtractTextFromContentArray(contentArray);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        return string.Empty;
    }

    private static bool TryExtractMessageText(JsonElement message, out string text)
    {
        if (message.TryGetProperty("content", out var content))
        {
            text = content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : ExtractTextFromContentArray(content);
            if (!string.IsNullOrWhiteSpace(text))
                return true;
        }

        if (message.TryGetProperty("reasoning_content", out var reasoningContent) &&
            reasoningContent.ValueKind == JsonValueKind.String)
        {
            text = reasoningContent.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (message.TryGetProperty("reasoning", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.String)
        {
            text = reasoning.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
        }

        text = string.Empty;
        return false;
    }

    private static string ExtractTextFromContentArray(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = content.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("output_text", out var outputText) &&
                    outputText.ValueKind == JsonValueKind.String)
                {
                    return outputText.GetString();
                }
                return null;
            })
            .Where(part => !string.IsNullOrWhiteSpace(part));

        return string.Join("\n", parts);
    }

    private static string TruncateForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 180 ? singleLine : $"{singleLine[..180]}...";
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
