using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.ViewModels;
using Perelegans.Models;
using Perelegans.Services;
using Perelegans.Views;
using Forms = System.Windows.Forms;

namespace Perelegans;

public partial class App : System.Windows.Application
{
    private ThemeService? _themeService;
    private ProcessMonitorService? _processMonitor;
    private SettingsService? _settingsService;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        // Initialize services
        var settingsService = new SettingsService();
        settingsService.Load();
        _settingsService = settingsService;

        _themeService = new ThemeService();
        _themeService.ApplyTheme(settingsService.Settings.Theme);
        TranslationService.Instance.ChangeLanguage(settingsService.Settings.Language);

        var dbService = new DatabaseService();
        _processMonitor = new ProcessMonitorService(dbService);
        var httpClient = new System.Net.Http.HttpClient();

        // Create MainViewModel with all services
        var mainVm = new MainViewModel(dbService, settingsService, _themeService, _processMonitor, httpClient, DialogCoordinator.Instance);

        // Create and show MainWindow
        _mainWindow = new MainWindow
        {
            DataContext = mainVm
        };
        MainWindow = _mainWindow;
        _mainWindow.Closing += MainWindow_OnClosing;
        _mainWindow.Show();

        InitializeTrayIcon();

        // Initialize async data (DB creation, load games, start monitor)
        await mainVm.InitializeAsync();
    }

    public void RequestShutdown()
    {
        _allowExit = true;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _processMonitor?.Stop();
        _themeService?.Dispose();
        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Perelegans",
            Visible = false,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowExit || _settingsService?.Settings.CloseBehavior != AppCloseBehavior.MinimizeToTray)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        if (_mainWindow == null || _trayIcon == null)
        {
            return;
        }

        UpdateTrayMenu();
        _mainWindow.ShowInTaskbar = false;
        _mainWindow.Hide();
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (_mainWindow == null || _trayIcon == null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void UpdateTrayMenu()
    {
        var menu = _trayIcon?.ContextMenuStrip;
        if (menu == null)
        {
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(TranslationService.Instance["Tray_Show"], null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(TranslationService.Instance["Tray_Exit"], null, (_, _) => Dispatcher.Invoke(RequestShutdown));
    }
}
