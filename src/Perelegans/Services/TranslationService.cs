using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Collections.Generic;
using System.Linq;
using Perelegans.i18n;

namespace Perelegans.Services;

public class TranslationService : INotifyPropertyChanged
{
    private const string DefaultCultureCode = "zh-Hans";
    private static readonly string[] SupportedCultureCodes = ["zh-Hans", "en-US", "ja-JP"];
    private static readonly Dictionary<string, string> LegacyCultureAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh-Hans",
        ["zh-CN"] = "zh-Hans",
        ["zh-Hans"] = "zh-Hans",
        ["中文"] = "zh-Hans",
        ["简体中文"] = "zh-Hans",
        ["Chinese"] = "zh-Hans",
        ["Chinese (Simplified)"] = "zh-Hans",
        ["en"] = "en-US",
        ["en-US"] = "en-US",
        ["English"] = "en-US",
        ["英文"] = "en-US",
        ["ja"] = "ja-JP",
        ["ja-JP"] = "ja-JP",
        ["jp"] = "ja-JP",
        ["Japanese"] = "ja-JP",
        ["日本語"] = "ja-JP",
        ["日文"] = "ja-JP"
    };

    private static TranslationService? _instance;
    public static TranslationService Instance => _instance ??= new TranslationService();

    private readonly ResourceManager _resourceManager;
    private CultureInfo _currentCulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    private TranslationService()
    {
        _resourceManager = new ResourceManager("Perelegans.i18n.Strings", typeof(TranslationService).Assembly);
        _currentCulture = CultureInfo.CurrentUICulture;
    }

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (!Equals(_currentCulture, value))
            {
                _currentCulture = value;
                CultureInfo.DefaultThreadCurrentUICulture = value;
                CultureInfo.DefaultThreadCurrentCulture = value;
                System.Threading.Thread.CurrentThread.CurrentUICulture = value;
                
                OnPropertyChanged("Item[]");
            }
        }
    }

    public void ChangeLanguage(string cultureCode)
    {
        CurrentCulture = CultureInfo.GetCultureInfo(NormalizeLanguageCode(cultureCode));
    }

    public static string NormalizeLanguageCode(string? cultureCode)
    {
        if (string.IsNullOrWhiteSpace(cultureCode))
        {
            return DefaultCultureCode;
        }

        var trimmed = cultureCode.Trim();

        if (LegacyCultureAliases.TryGetValue(trimmed, out var mapped))
        {
            return mapped;
        }

        var supportedMatch = SupportedCultureCodes.FirstOrDefault(code =>
            string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase));

        if (supportedMatch != null)
        {
            return supportedMatch;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(trimmed);

            supportedMatch = SupportedCultureCodes.FirstOrDefault(code =>
                string.Equals(code, culture.Name, StringComparison.OrdinalIgnoreCase));

            if (supportedMatch != null)
            {
                return supportedMatch;
            }

            if (LegacyCultureAliases.TryGetValue(culture.TwoLetterISOLanguageName, out mapped))
            {
                return mapped;
            }
        }
        catch (CultureNotFoundException)
        {
            // Fall back to the default UI culture below.
        }

        return DefaultCultureCode;
    }

    public string this[string key]
    {
        get
        {
            var str = _resourceManager.GetString(key, CurrentCulture);
            return string.IsNullOrEmpty(str) ? $"[{key}]" : str;
        }
    }

    public string GetString(string key) => this[key];

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
