using System.Windows;
using Perelegans.Services;
using Perelegans.ViewModels;
using Perelegans.Models;
using Perelegans.Views;

namespace Perelegans;

public partial class App : Application
{
    private ThemeService? _themeService;
    private ProcessMonitorService? _processMonitor;

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        // Initialize services
        var settingsService = new SettingsService();
        settingsService.Load();

        _themeService = new ThemeService();
        _themeService.ApplyTheme(settingsService.Settings.Theme);
        TranslationService.Instance.ChangeLanguage(settingsService.Settings.Language);

        var dbService = new DatabaseService();
        _processMonitor = new ProcessMonitorService(dbService);
        var httpClient = new System.Net.Http.HttpClient();

        // Create MainViewModel with all services
        var mainVm = new MainViewModel(dbService, settingsService, _themeService, _processMonitor, httpClient);

        // Create and show MainWindow
        var mainWindow = new MainWindow
        {
            DataContext = mainVm
        };
        mainWindow.Show();

        // Initialize async data (DB creation, load games, start monitor)
        await mainVm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _processMonitor?.Stop();
        _themeService?.Dispose();
        base.OnExit(e);
    }
}
