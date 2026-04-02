using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Perelegans.i18n;

namespace Perelegans.Services;

public class TranslationService : INotifyPropertyChanged
{
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
        CurrentCulture = new CultureInfo(cultureCode);
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
