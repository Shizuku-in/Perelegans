using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Models;
using Perelegans.Services;
using Perelegans.Views;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace Perelegans.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly ProcessMonitorService _processMonitor;
    private HttpClient _httpClient;
    private readonly IDialogCoordinator _dialogCoordinator;

    [ObservableProperty]
    private ObservableCollection<Game> _games = new();

    [ObservableProperty]
    private Game? _selectedGame;

    [ObservableProperty]
    private bool _isAboutOverlayVisible;

    public string TotalPlaytimeText
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var g in Games)
                total += g.Playtime;
            int hours = (int)total.TotalHours;
            int mins = total.Minutes;
            return hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
        }
    }

    public int CompletedCount => Games.Count(g => g.Status == GameStatus.Completed);

    public MainViewModel(
        DatabaseService dbService,
        SettingsService settingsService,
        ThemeService themeService,
        ProcessMonitorService processMonitor,
        HttpClient httpClient,
        IDialogCoordinator dialogCoordinator)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _themeService = themeService;
        _processMonitor = processMonitor;
        _httpClient = httpClient;
        _dialogCoordinator = dialogCoordinator;

        // Subscribe to process monitor events
        _processMonitor.PlaytimeUpdated += OnPlaytimeUpdated;
        _processMonitor.GameDetectionChanged += OnGameDetectionChanged;
        AttachGamesCollection(Games);
    }

    /// <summary>
    /// Loads games from the database.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbService.EnsureDatabaseCreatedAsync();

        var games = await _dbService.GetAllGamesAsync();
        ReplaceGames(games);

        // Start process monitor
        var settings = _settingsService.Settings;
        _processMonitor.SetInterval(settings.MonitorIntervalSeconds);
        if (settings.MonitorEnabled)
        {
            _processMonitor.Start();
        }
    }

    private void OnPlaytimeUpdated(int gameId, TimeSpan elapsed)
    {
        var game = Games.FirstOrDefault(g => g.Id == gameId);
        if (game != null)
        {
            game.Playtime += elapsed;
            game.AccessedDate = DateTime.Now;
            RefreshStats();
        }
    }

    private void OnGameDetectionChanged(int gameId, bool isDetectedRunning)
    {
        var game = Games.FirstOrDefault(g => g.Id == gameId);
        if (game != null)
        {
            game.IsDetectedRunning = isDetectedRunning;
        }
    }

    private void AttachGamesCollection(ObservableCollection<Game> games)
    {
        games.CollectionChanged -= OnGamesCollectionChanged;
        games.CollectionChanged += OnGamesCollectionChanged;
    }

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshStats();
        _processMonitor.UpdateMonitoredGames(Games);
    }

    private void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalPlaytimeText));
        OnPropertyChanged(nameof(CompletedCount));
    }

    public void RefreshUi()
    {
        RefreshStats();
    }

    private void ReplaceGames(IEnumerable<Game> games)
    {
        Games.CollectionChanged -= OnGamesCollectionChanged;
        Games = new ObservableCollection<Game>(games);
        AttachGamesCollection(Games);
        SelectedGame = null;
        RefreshStats();
        _processMonitor.UpdateMonitoredGames(Games);
    }

    // ---- Menu Commands (File) ----

    [RelayCommand]
    private async Task AddFromProcess()
    {
        var vm = new AddFromProcessViewModel();
        var win = new AddFromProcessWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true && vm.SelectedProcess != null)
        {
            var newGame = new Game
            {
                Title = vm.SelectedProcess.WindowTitle,
                ProcessName = vm.SelectedProcess.ProcessName,
                ExecutablePath = vm.SelectedProcess.ExecutablePath
            };
            await _dbService.AddGameAsync(newGame);
            Games.Add(newGame);
        }
    }

    [RelayCommand]
    private async Task AddFromWebsite()
    {
        var newGame = new Game { Title = "New Game" };
        var vm = new MetadataViewModel(newGame, _httpClient, _dbService, isNewGame: true, isSearchEnabled: true);
        var win = new MetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            await _dbService.AddGameAsync(newGame);
            Games.Insert(0, newGame);
            SelectedGame = newGame;
        }
    }

    [RelayCommand]
    private async Task SaveBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "SQLite Database (*.db)|*.db",
            FileName = "perelegans_backup.db"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _dbService.BackupDatabaseAsync(dialog.FileName);
                await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_BackupSuccess"]);
            }
            catch (Exception ex)
            {
                await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_ErrorTitle"], string.Format(TranslationService.Instance["Msg_BackupFailed"], ex.Message));
            }
        }
    }

    [RelayCommand]
    private async Task RestoreBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "SQLite Database (*.db)|*.db"
        };
        if (dialog.ShowDialog() == true)
        {
            var shouldResumeMonitor = _processMonitor.IsRunning;

            try
            {
                if (shouldResumeMonitor)
                {
                    await _processMonitor.StopAsync();
                }

                await _dbService.RestoreDatabaseAsync(dialog.FileName);
                var games = await _dbService.GetAllGamesAsync();
                ReplaceGames(games);
                await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_RestoreSuccess"]);
            }
            catch (Exception ex)
            {
                await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_ErrorTitle"], string.Format(TranslationService.Instance["Msg_RestoreFailed"], ex.Message));
            }
            finally
            {
                if (shouldResumeMonitor && _settingsService.Settings.MonitorEnabled && !_processMonitor.IsRunning)
                {
                    _processMonitor.UpdateMonitoredGames(Games);
                    _processMonitor.Start();
                }
            }
        }
    }

    [RelayCommand]
    private void ExitApp()
    {
        if (Application.Current is Perelegans.App app)
        {
            app.RequestShutdown();
        }
        else
        {
            Application.Current.Shutdown();
        }
    }

    public bool IsMonitorEnabled => _settingsService.Settings.MonitorEnabled;

    // ---- Menu Commands (Tool) ----

    [RelayCommand]
    private void MonitorProcess()
    {
        var settings = _settingsService.Settings;
        settings.MonitorEnabled = !settings.MonitorEnabled;
        _settingsService.Save();

        if (settings.MonitorEnabled && !_processMonitor.IsRunning)
            _processMonitor.Start();
        else if (!settings.MonitorEnabled && _processMonitor.IsRunning)
            _processMonitor.Stop();

        OnPropertyChanged(nameof(IsMonitorEnabled));
    }

    [RelayCommand]
    private async Task PlaytimeStatistics()
    {
        var vm = new PlaytimeStatsViewModel(_dbService, _processMonitor);
        await vm.RefreshAsync();

        var win = new PlaytimeStatsWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        win.ShowDialog();
    }

    [RelayCommand]
    private void OpenRecommendations()
    {
        var vm = new RecommendationViewModel(
            _dbService,
            _settingsService,
            _httpClient,
            _dialogCoordinator,
            importedGame =>
            {
                Games.Insert(0, importedGame);
                SelectedGame = importedGame;
                RefreshStats();
                _processMonitor.UpdateMonitoredGames(Games);
            });

        var win = new RecommendationWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        win.ShowDialog();
    }

    // ---- Menu Commands (Help) ----

    [RelayCommand]
    private async Task ShowAbout()
    {
        await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AboutTitle"], TranslationService.Instance["Msg_AboutText"]);
    }

    // ---- Settings ----

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsVm = new SettingsViewModel(_themeService, _settingsService, new StartupRegistrationService());
        var settingsWindow = new SettingsWindow
        {
            DataContext = settingsVm,
            Owner = Application.Current.MainWindow
        };

        if (settingsWindow.ShowDialog() == true)
        {
            // Settings were saved - update monitor
            var settings = _settingsService.Settings;
            _processMonitor.SetInterval(settings.MonitorIntervalSeconds);
            var previousClient = _httpClient;
            _httpClient = MetadataHttpClientFactory.Create(settings);
            previousClient.Dispose();

            if (settings.MonitorEnabled && !_processMonitor.IsRunning)
                _processMonitor.Start();
            else if (!settings.MonitorEnabled && _processMonitor.IsRunning)
                _processMonitor.Stop();
        }
    }

    // ---- Context Menu Commands ----

    [RelayCommand]
    private async Task StartGame()
    {
        if (SelectedGame == null || string.IsNullOrWhiteSpace(SelectedGame.ExecutablePath))
        {
            await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_NoExecPath"]);
            return;
        }
        
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SelectedGame.ExecutablePath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(SelectedGame.ExecutablePath) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_ErrorTitle"], string.Format(TranslationService.Instance["Msg_GameStartFailed"], ex.Message));
        }
    }

    [RelayCommand]
    private void FetchMetadata()
    {
        if (SelectedGame == null) return;
        var vm = new MetadataViewModel(SelectedGame, _httpClient, _dbService, isNewGame: false, isSearchEnabled: true);
        var win = new MetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            var updated = _dbService.GetGameByIdAsync(SelectedGame.Id).Result;
            if (updated != null)
            {
                var index = Games.IndexOf(SelectedGame);
                if (index >= 0)
                {
                    Games[index] = updated;
                    SelectedGame = updated;
                }
            }
        }
    }

    [RelayCommand]
    private void EditMetadata()
    {
        if (SelectedGame == null) return;
        var vm = new MetadataViewModel(SelectedGame, _httpClient, _dbService, isNewGame: false, isSearchEnabled: false);
        var win = new MetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            var updated = _dbService.GetGameByIdAsync(SelectedGame.Id).Result;
            if (updated != null)
            {
                var index = Games.IndexOf(SelectedGame);
                if (index >= 0)
                {
                    Games[index] = updated;
                    SelectedGame = updated;
                }
            }
        }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (SelectedGame == null || string.IsNullOrWhiteSpace(SelectedGame.ExecutablePath))
            return;

        var dir = System.IO.Path.GetDirectoryName(SelectedGame.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch { }
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task OpenVndb()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.VndbId != null ? $"https://vndb.org/v{SelectedGame.VndbId.Replace("v", "")}" : null;
        if (url != null) OpenUrl(url); else await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_NoVndbId"]);
    }

    [RelayCommand]
    private async Task OpenErogameSpace()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.ErogameSpaceId != null ? $"https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game={SelectedGame.ErogameSpaceId}" : null;
        if (url != null) OpenUrl(url); else await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_NoEgsId"]);
    }

    [RelayCommand]
    private async Task OpenBangumi()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.BangumiId != null ? $"https://bgm.tv/subject/{SelectedGame.BangumiId}" : null;
        if (url != null) OpenUrl(url); else await _dialogCoordinator.ShowMessageAsync(this, TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_NoBangumiId"]);
    }

    [RelayCommand]
    private void OpenOfficialSite()
    {
        if (SelectedGame == null) return;
        OpenUrl(SelectedGame.OfficialWebsite);
    }

    [RelayCommand]
    private void OpenGamePlaytimeStats()
    {
        if (SelectedGame == null) return;
        var vm = new GamePlayLogViewModel(_dbService, SelectedGame);
        var win = new GamePlayLogWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    [RelayCommand]
    private void CopyTitle()
    {
        if (SelectedGame == null) return;
        Clipboard.SetText(SelectedGame.Title);
    }

    [RelayCommand]
    private void CopyBrand()
    {
        if (SelectedGame == null) return;
        Clipboard.SetText(SelectedGame.Brand);
    }

    [RelayCommand]
    private async Task DeleteGame()
    {
        if (SelectedGame == null) return;
        var result = await _dialogCoordinator.ShowMessageAsync(this,
            TranslationService.Instance["Msg_DeleteConfirmTitle"],
            string.Format(TranslationService.Instance["Msg_DeleteConfirmText"], SelectedGame.Title),
            MessageDialogStyle.AffirmativeAndNegative);
        if (result == MessageDialogResult.Affirmative)
        {
            await _dbService.DeleteGameAsync(SelectedGame.Id);
            Games.Remove(SelectedGame);
            _processMonitor.UpdateMonitoredGames(Games);
        }
    }

    [RelayCommand]
    private void SelectAllGames()
    {
        foreach (var g in Games)
            g.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllGames()
    {
        foreach (var g in Games)
            g.IsSelected = false;
    }

    [RelayCommand]
    private async Task DeleteSelectedGames()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialogCoordinator.ShowMessageAsync(this,
                TranslationService.Instance["Msg_AppTitle"],
                TranslationService.Instance["Msg_NoSelection"]);
            return;
        }

        var result = await _dialogCoordinator.ShowMessageAsync(this,
            TranslationService.Instance["Msg_DeleteConfirmTitle"],
            string.Format(TranslationService.Instance["Msg_DeleteSelectedConfirmText"], selected.Count),
            MessageDialogStyle.AffirmativeAndNegative);

        if (result != MessageDialogResult.Affirmative)
            return;

        foreach (var g in selected)
        {
            await _dbService.DeleteGameAsync(g.Id);
            Games.Remove(g);
        }

        _processMonitor.UpdateMonitoredGames(Games);
        RefreshStats();
    }

    [RelayCommand]
    private void OpenBatchMetadata()
    {
        var vm = new BatchMetadataViewModel(_dbService, _httpClient, _dialogCoordinator);
        var win = new BatchMetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }
}
