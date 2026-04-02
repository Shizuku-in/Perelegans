using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;
using Perelegans.Views;

namespace Perelegans.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly ProcessMonitorService _processMonitor;
    private readonly HttpClient _httpClient;

    [ObservableProperty]
    private ObservableCollection<Game> _games = new();

    [ObservableProperty]
    private Game? _selectedGame;

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
        HttpClient httpClient)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _themeService = themeService;
        _processMonitor = processMonitor;
        _httpClient = httpClient;

        // Subscribe to process monitor events
        _processMonitor.PlaytimeUpdated += OnPlaytimeUpdated;

        Games.CollectionChanged += (_, _) => RefreshStats();
    }

    /// <summary>
    /// Loads games from the database. If empty, inserts sample data.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbService.EnsureDatabaseCreatedAsync();

        var games = await _dbService.GetAllGamesAsync();
        if (games.Count == 0)
        {
            // Insert sample data on first run
            await InsertSampleDataAsync();
            games = await _dbService.GetAllGamesAsync();
        }

        Games = new ObservableCollection<Game>(games);
        Games.CollectionChanged += (_, _) => RefreshStats();
        RefreshStats();

        // Start process monitor
        _processMonitor.UpdateMonitoredGames(Games);
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

    private void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalPlaytimeText));
        OnPropertyChanged(nameof(CompletedCount));
    }

    private async Task InsertSampleDataAsync()
    {
        var sampleGames = new[]
        {
            new Game
            {
                Title = "Summer Pockets",
                Brand = "Key",
                ReleaseDate = new DateTime(2018, 6, 29),
                Status = GameStatus.Completed,
                Playtime = new TimeSpan(32, 15, 0),
                CreatedDate = new DateTime(2024, 1, 15),
                AccessedDate = new DateTime(2024, 2, 20)
            },
            new Game
            {
                Title = "WHITE ALBUM2 〜closing chapter〜",
                Brand = "Leaf",
                ReleaseDate = new DateTime(2012, 12, 20),
                Status = GameStatus.Playing,
                Playtime = new TimeSpan(18, 30, 0),
                CreatedDate = new DateTime(2024, 3, 1),
                AccessedDate = new DateTime(2024, 3, 25)
            },
            new Game
            {
                Title = "Fate/stay night",
                Brand = "TYPE-MOON",
                ReleaseDate = new DateTime(2004, 1, 30),
                Status = GameStatus.Completed,
                Playtime = new TimeSpan(65, 0, 0),
                CreatedDate = new DateTime(2023, 6, 10),
                AccessedDate = new DateTime(2023, 9, 5)
            },
            new Game
            {
                Title = "Steins;Gate",
                Brand = "MAGES.",
                ReleaseDate = new DateTime(2009, 10, 15),
                Status = GameStatus.Completed,
                Playtime = new TimeSpan(28, 45, 0),
                CreatedDate = new DateTime(2023, 11, 1),
                AccessedDate = new DateTime(2023, 12, 15)
            },
            new Game
            {
                Title = "CLANNAD",
                Brand = "Key",
                ReleaseDate = new DateTime(2004, 4, 28),
                Status = GameStatus.Abandoned,
                Playtime = new TimeSpan(12, 10, 0),
                CreatedDate = new DateTime(2024, 4, 1),
                AccessedDate = new DateTime(2024, 4, 10)
            },
            new Game
            {
                Title = "Muv-Luv Alternative",
                Brand = "âge",
                ReleaseDate = new DateTime(2006, 2, 24),
                Status = GameStatus.Playing,
                Playtime = new TimeSpan(45, 20, 0),
                CreatedDate = new DateTime(2024, 2, 10),
                AccessedDate = new DateTime(2024, 5, 1)
            }
        };

        foreach (var game in sampleGames)
        {
            await _dbService.AddGameAsync(game);
        }
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
        var vm = new MetadataViewModel(newGame, _httpClient, _dbService, true);
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

    private string GetDatabasePath()
    {
        var appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Perelegans");
        return System.IO.Path.Combine(appData, "perelegans.db");
    }

    [RelayCommand]
    private void SaveBackup()
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
                System.IO.File.Copy(GetDatabasePath(), dialog.FileName, true);
                MessageBox.Show(TranslationService.Instance["Msg_BackupSuccess"], TranslationService.Instance["Msg_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(TranslationService.Instance["Msg_BackupFailed"], ex.Message), TranslationService.Instance["Msg_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
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
            try
            {
                System.IO.File.Copy(dialog.FileName, GetDatabasePath(), true);
                Games.Clear();
                var games = await _dbService.GetAllGamesAsync();
                foreach (var g in games) Games.Add(g);
                MessageBox.Show(TranslationService.Instance["Msg_RestoreSuccess"], TranslationService.Instance["Msg_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(TranslationService.Instance["Msg_RestoreFailed"], ex.Message), TranslationService.Instance["Msg_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ExitApp()
    {
        _processMonitor.Stop();
        Application.Current.Shutdown();
    }

    // ---- Menu Commands (Tool) ----

    [RelayCommand]
    private void MonitorProcess()
    {
        OpenSettings();
    }

    [RelayCommand]
    private void PlaytimeStatistics()
    {
        var vm = new PlaytimeStatsViewModel(_dbService);
        var win = new PlaytimeStatsWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };
        _ = vm.InitializeAsync();
        win.ShowDialog();
    }

    // ---- Menu Commands (Help) ----

    [RelayCommand]
    private void ShowAbout()
    {
        MessageBox.Show(TranslationService.Instance["Msg_AboutText"], TranslationService.Instance["Msg_AboutTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Settings ----

    [RelayCommand]
    private void OpenSettings()
    {
        var settingsVm = new SettingsViewModel(_themeService, _settingsService);
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

            if (settings.MonitorEnabled && !_processMonitor.IsRunning)
                _processMonitor.Start();
            else if (!settings.MonitorEnabled && _processMonitor.IsRunning)
                _processMonitor.Stop();
        }
    }

    // ---- Context Menu Commands ----

    [RelayCommand]
    private void StartGame()
    {
        if (SelectedGame == null || string.IsNullOrWhiteSpace(SelectedGame.ExecutablePath))
        {
            MessageBox.Show(TranslationService.Instance["Msg_NoExecPath"], TranslationService.Instance["Msg_AppTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(string.Format(TranslationService.Instance["Msg_GameStartFailed"], ex.Message), TranslationService.Instance["Msg_ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void FetchMetadata()
    {
        if (SelectedGame == null) return;
        var vm = new MetadataViewModel(SelectedGame, _httpClient, _dbService);
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
        var vm = new MetadataViewModel(SelectedGame, _httpClient, _dbService, false);
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
    private void OpenVndb()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.VndbId != null ? $"https://vndb.org/v{SelectedGame.VndbId.Replace("v", "")}" : null;
        if (url != null) OpenUrl(url); else MessageBox.Show(TranslationService.Instance["Msg_NoVndbId"], TranslationService.Instance["Msg_AppTitle"]);
    }

    [RelayCommand]
    private void OpenErogameSpace()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.ErogameSpaceId != null ? $"https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game={SelectedGame.ErogameSpaceId}" : null;
        if (url != null) OpenUrl(url); else MessageBox.Show(TranslationService.Instance["Msg_NoEgsId"], TranslationService.Instance["Msg_AppTitle"]);
    }

    [RelayCommand]
    private void OpenBangumi()
    {
        if (SelectedGame == null) return;
        var url = SelectedGame.BangumiId != null ? $"https://bgm.tv/subject/{SelectedGame.BangumiId}" : null;
        if (url != null) OpenUrl(url); else MessageBox.Show(TranslationService.Instance["Msg_NoBangumiId"], TranslationService.Instance["Msg_AppTitle"]);
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
        var result = MessageBox.Show(string.Format(TranslationService.Instance["Msg_DeleteConfirmText"], SelectedGame.Title),
            TranslationService.Instance["Msg_DeleteConfirmTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _dbService.DeleteGameAsync(SelectedGame.Id);
            Games.Remove(SelectedGame);
            _processMonitor.UpdateMonitoredGames(Games);
        }
    }
}
