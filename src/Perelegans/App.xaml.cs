using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
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
    private const string SingleInstanceMutexName = @"Local\Perelegans.SingleInstance";
    private const string SingleInstancePipeName = "Perelegans.SingleInstance";
    private const string ActivationMessage = "ACTIVATE";

    private ThemeService? _themeService;
    private ProcessMonitorService? _processMonitor;
    private SettingsService? _settingsService;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;
    private volatile bool _pendingActivationRequest;
    private System.Net.Http.HttpClient? _metadataHttpClient;

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        if (!TryAcquireSingleInstanceLock())
        {
            await NotifyExistingInstanceAsync();
            Shutdown();
            return;
        }

        _activationListenerCts = new CancellationTokenSource();
        _activationListenerTask = Task.Run(() => ListenForActivationAsync(_activationListenerCts.Token));

        // Initialize services
        var settingsService = new SettingsService();
        settingsService.Load();
        _settingsService = settingsService;

        _themeService = new ThemeService();
        _themeService.ApplyTheme(settingsService.Settings.Theme);
        TranslationService.Instance.ChangeLanguage(settingsService.Settings.Language);

        var dbService = new DatabaseService();
        _processMonitor = new ProcessMonitorService(dbService);
        _metadataHttpClient = MetadataHttpClientFactory.Create(settingsService.Settings);

        // Create MainViewModel with all services
        var mainVm = new MainViewModel(dbService, settingsService, _themeService, _processMonitor, _metadataHttpClient, DialogCoordinator.Instance);

        // Create and show MainWindow
        _mainWindow = new MainWindow
        {
            DataContext = mainVm
        };
        MainWindow = _mainWindow;
        _mainWindow.Closing += MainWindow_OnClosing;
        _mainWindow.Show();

        if (_pendingActivationRequest)
        {
            _pendingActivationRequest = false;
            RestoreFromTray();
        }

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
        if (_activationListenerCts != null)
        {
            _activationListenerCts.Cancel();
            _activationListenerCts.Dispose();
            _activationListenerCts = null;
        }

        _activationListenerTask = null;

        if (_singleInstanceMutex != null)
        {
            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Ignore release errors during shutdown.
                }
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _metadataHttpClient?.Dispose();
        _processMonitor?.Stop();
        _themeService?.Dispose();
        base.OnExit(e);
    }

    public static void WriteCrashLog(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Perelegans");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, "error.log");
            File.AppendAllText(
                logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // Last-resort logging should never crash the app.
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        System.Windows.MessageBox.Show(
            e.Exception.ToString(),
            TranslationService.Instance["Msg_ErrorTitle"],
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog(ex);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.SetObserved();
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
        if (_mainWindow == null)
        {
            return;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }

        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private bool TryAcquireSingleInstanceLock()
    {
        try
        {
            _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);

            try
            {
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
            }

            if (_ownsSingleInstanceMutex)
            {
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch
        {
            // Allow startup even if OS-level synchronization cannot be initialized.
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
            return true;
        }
    }

    private static async Task NotifyExistingInstanceAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(client)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(ActivationMessage).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No active primary instance listener available in timeout window.
        }
        catch (IOException)
        {
            // The listener may be restarting; ignore and exit current instance.
        }
        catch (UnauthorizedAccessException)
        {
            // Access can fail in restricted environments; ignore and exit current instance.
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    SingleInstancePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync().ConfigureAwait(false);

                if (!string.Equals(message, ActivationMessage, StringComparison.Ordinal))
                {
                    continue;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_mainWindow == null)
                    {
                        _pendingActivationRequest = true;
                        return;
                    }

                    RestoreFromTray();
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Ignore transient pipe failures and keep listening.
            }
        }
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
