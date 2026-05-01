using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
    private const int DefaultPageSize = 12;
    private const int MaxVisibleCoverRefreshCount = 12;
    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly ProcessMonitorService _processMonitor;
    private HttpClient _httpClient;
    private readonly IDialogCoordinator _dialogCoordinator;
    private BulkDeleteWindow? _bulkDeleteWindow;
    private int _loadedGameCount;
    private int _materializedGameCount;
    private int _visibleRefreshVersion;
    private int _profileRefreshVersion;
    private HashSet<int>? _assistantFilterIds;

    [ObservableProperty]
    private ObservableCollection<Game> _games = new();

    [ObservableProperty]
    private ObservableCollection<ObservableCollection<Game>> _visibleRows = new();

    [ObservableProperty]
    private Game? _selectedGame;

    [ObservableProperty]
    private bool _isAboutOverlayVisible;

    [ObservableProperty]
    private bool _isAssistantPanelVisible;

    [ObservableProperty]
    private AiAssistantViewModel? _assistantViewModel;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = DefaultPageSize;

    [ObservableProperty]
    private int _columnCount = 4;


    public string TotalPlaytimeText
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var g in Games)
                total += g.Playtime;
            return PlaytimeTextFormatter.Format(total);
        }
    }

    public int CompletedCount => Games.Count(g => g.Status == GameStatus.Completed);
    public int TotalGameCount => Games.Count;
    public bool CanLoadMoreGames => _materializedGameCount < GetDisplayGames().Count;

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
        TranslationService.Instance.PropertyChanged += OnTranslationChanged;
        AttachGamesCollection(Games);
    }

    /// <summary>
    /// Loads games from the database.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbService.EnsureDatabaseCreatedAsync();

        var games = (await _dbService.GetAllGamesAsync()).ToList();
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
            SortGamesForDisplay();
            RefreshStats();
        }
    }

    private void OnGameDetectionChanged(int gameId, bool isDetectedRunning)
    {
        var game = Games.FirstOrDefault(g => g.Id == gameId);
        if (game != null)
        {
            game.IsDetectedRunning = isDetectedRunning;
            SortGamesForDisplay();
            if (!isDetectedRunning)
            {
                QueueRecommendationProfileRefresh();
            }
        }
    }

    private void AttachGamesCollection(ObservableCollection<Game> games)
    {
        games.CollectionChanged -= OnGamesCollectionChanged;
        games.CollectionChanged += OnGamesCollectionChanged;
    }

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildVisibleRows();
        RefreshStats();
        _processMonitor.UpdateMonitoredGames(Games);
    }

    private void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalPlaytimeText));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(TotalGameCount));
    }

    private void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
        {
            RefreshStats();
        }
    }

    public void RefreshUi()
    {
        RefreshStats();
    }

    private void ReplaceGames(IEnumerable<Game> games)
    {
        Games.CollectionChanged -= OnGamesCollectionChanged;
        Games = new ObservableCollection<Game>(OrderGamesForDisplay(games));
        AttachGamesCollection(Games);
        _loadedGameCount = Math.Min(Games.Count, Math.Max(PageSize, _loadedGameCount));
        RebuildVisibleRows();
        if (SelectedGame != null && !Games.Contains(SelectedGame))
        {
            SelectedGame = null;
        }
        RefreshStats();
        _processMonitor.UpdateMonitoredGames(Games);
    }

    private void SortGamesForDisplay()
    {
        if (Games.Count <= 1)
            return;

        var orderedGames = OrderGamesForDisplay(Games).ToList();
        if (Games.SequenceEqual(orderedGames))
            return;

        ReplaceGames(orderedGames);
    }

    private static IEnumerable<Game> OrderGamesForDisplay(IEnumerable<Game> games)
    {
        return games
            .OrderByDescending(game => game.IsDetectedRunning)
            .ThenByDescending(game => game.AccessedDate)
            .ThenByDescending(game => game.Id);
    }

    private void HandleMetadataSaved(Game? game)
    {
        if (game == null)
            return;

        game.CoverAspectRatio ??= CoverArtService.TryReadCoverAspectRatio(game.CoverImagePath);
        game.RefreshCoverBindings();
        RefreshStats();
        _processMonitor.UpdateMonitoredGames(Games);
        QueueRecommendationProfileRefresh();
    }

    private void RebuildVisibleRows()
    {
        var displayGames = GetDisplayGames();
        var targetCount = Math.Min(displayGames.Count, Math.Max(PageSize, _loadedGameCount));
        var version = Interlocked.Increment(ref _visibleRefreshVersion);
        _loadedGameCount = targetCount;
        VisibleRows.Clear();
        _materializedGameCount = 0;

        OnPropertyChanged(nameof(CanLoadMoreGames));
        AppendVisibleGames(targetCount, version);
    }

    private void AppendVisibleGames(int targetCount, int version)
    {
        if (_materializedGameCount >= targetCount)
            return;

        ObservableCollection<Game>? currentRow = VisibleRows.LastOrDefault();
        if (currentRow != null && currentRow.Count >= ColumnCount)
        {
            currentRow = null;
        }

        var appendedGames = new List<Game>();
        foreach (var game in GetDisplayGames().Skip(_materializedGameCount).Take(targetCount - _materializedGameCount))
        {
            currentRow ??= AddRow();
            currentRow.Add(game);
            appendedGames.Add(game);
            _materializedGameCount++;

            if (currentRow.Count >= ColumnCount)
            {
                currentRow = null;
            }
        }

        OnPropertyChanged(nameof(CanLoadMoreGames));
        _ = PrimeVisibleGamesAsync(appendedGames, version);
    }

    private ObservableCollection<Game> AddRow()
    {
        var row = new ObservableCollection<Game>();
        VisibleRows.Add(row);
        return row;
    }

    private async Task PrimeVisibleGamesAsync(IReadOnlyList<Game> games, int version)
    {
        if (games.Count == 0)
            return;

        foreach (var game in games)
        {
            if (!game.CoverAspectRatio.HasValue)
            {
                game.CoverAspectRatio = CoverArtService.TryReadCoverAspectRatio(game.CoverImagePath);
            }
        }

        var coverArtService = new CoverArtService(_httpClient);

        foreach (var game in games.Where(RequiresCoverRefresh).Take(MaxVisibleCoverRefreshCount))
        {
            var cachedPath = await coverArtService.ResolveAndCacheCoverAsync(game);
            if (string.IsNullOrWhiteSpace(cachedPath))
                continue;

            game.RefreshCoverBindings();

            try
            {
                await _dbService.UpdateGameAsync(game);
            }
            catch
            {
            }

            if (version != _visibleRefreshVersion)
                return;
        }
    }

    private async Task AddImportedGameAsync(Game game, bool insertAtTop, bool selectAfterInsert = true)
    {
        await _dbService.AddGameAsync(game);
        InsertImportedGameIntoCollection(game, insertAtTop, selectAfterInsert);
        QueueRecommendationProfileRefresh();
        _ = AutoFetchCoverForImportedGameAsync(game);
    }

    private void InsertImportedGameIntoCollection(Game game, bool insertAtTop, bool selectAfterInsert = true)
    {
        Games.Add(game);
        SortGamesForDisplay();

        if (insertAtTop || selectAfterInsert)
        {
            _loadedGameCount = Math.Max(_loadedGameCount, PageSize);
        }

        if (selectAfterInsert)
        {
            SelectedGame = game;
        }
    }

    private async Task AutoFetchCoverForImportedGameAsync(Game game)
    {
        try
        {
            if (!RequiresCoverRefresh(game))
                return;

            var coverArtService = new CoverArtService(_httpClient);
            var cachedPath = await coverArtService.ResolveAndCacheCoverAsync(game);
            if (string.IsNullOrWhiteSpace(cachedPath))
                return;

            game.RefreshCoverBindings();
            await _dbService.UpdateGameAsync(game);
        }
        catch
        {
        }
    }

    private static bool RequiresCoverRefresh(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.CoverDisplaySource))
            return true;

        return false;
    }

    public void UpdatePageSize(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        var columns = Math.Max(1, (int)Math.Floor((viewportWidth + 14) / 210d));
        var rows = Math.Max(1, (int)Math.Floor((viewportHeight + 14) / 292d));
        var targetPageSize = Math.Clamp(columns * Math.Max(2, rows + 1), 8, 24);

        var previousColumns = ColumnCount;
        if (targetPageSize == PageSize && columns == previousColumns)
            return;

        ColumnCount = columns;
        PageSize = targetPageSize;
        _loadedGameCount = Math.Max(_loadedGameCount, PageSize);
        if (previousColumns != ColumnCount)
        {
            RebuildVisibleRows();
        }
        else
        {
            AppendVisibleGames(Math.Min(GetDisplayGames().Count, _loadedGameCount), _visibleRefreshVersion);
        }
    }

    public void LoadMoreGames()
    {
        if (!CanLoadMoreGames)
            return;

        _loadedGameCount = Math.Min(GetDisplayGames().Count, _loadedGameCount + PageSize);
        AppendVisibleGames(_loadedGameCount, _visibleRefreshVersion);
    }

    private IReadOnlyList<Game> GetDisplayGames()
    {
        if (_assistantFilterIds == null)
            return Games;

        return Games.Where(game => _assistantFilterIds.Contains(game.Id)).ToList();
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
            await AddImportedGameAsync(newGame, insertAtTop: false, selectAfterInsert: false);
        }
    }

    [RelayCommand]
    private async Task AddFromWebsite()
    {
        var newGame = new Game { Title = TranslationService.Instance["Game_DefaultTitle"] };
        var vm = new MetadataViewModel(newGame, _httpClient, _dbService, _settingsService, isNewGame: true, isSearchEnabled: true);
        var win = new MetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            await AddImportedGameAsync(newGame, insertAtTop: true);
        }
    }

    [RelayCommand]
    private async Task SaveBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = TranslationService.Instance["Dialog_SqliteDatabaseFilter"],
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
            Filter = TranslationService.Instance["Dialog_SqliteDatabaseFilter"]
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
                _loadedGameCount = 0;
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
            async importedGame =>
            {
                InsertImportedGameIntoCollection(importedGame, insertAtTop: true);
                await AutoFetchCoverForImportedGameAsync(importedGame);
            });

        var win = new RecommendationWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        win.ShowDialog();
    }

    [RelayCommand]
    private void OpenAiAssistant()
    {
        EnsureAssistantViewModel();
        IsAssistantPanelVisible = !IsAssistantPanelVisible;
    }

    [RelayCommand]
    private void CloseAssistantPanel()
    {
        IsAssistantPanelVisible = false;
    }

    private void EnsureAssistantViewModel()
    {
        AssistantViewModel ??= new AiAssistantViewModel(
            _httpClient,
            _settingsService,
            Games,
            SelectAssistantGame,
            OpenAssistantGameMetadata,
            ApplyAssistantGameFilter);
    }

    private void SelectAssistantGame(int gameId)
    {
        var game = Games.FirstOrDefault(item => item.Id == gameId);
        if (game == null)
            return;

        if (_assistantFilterIds != null && !_assistantFilterIds.Contains(game.Id))
        {
            _assistantFilterIds = null;
            RebuildVisibleRows();
        }

        EnsureGameVisible(game);
        SelectedGame = game;
    }

    private void OpenAssistantGameMetadata(int gameId)
    {
        var game = Games.FirstOrDefault(item => item.Id == gameId);
        if (game == null)
            return;

        SelectAssistantGame(gameId);
        OpenMetadataForGame(game, isSearchEnabled: false);
    }

    private void ApplyAssistantGameFilter(IReadOnlyCollection<int> gameIds)
    {
        _assistantFilterIds = gameIds.Count == 0 ? null : gameIds.ToHashSet();
        _loadedGameCount = Math.Max(PageSize, Math.Min(GetDisplayGames().Count, _loadedGameCount));
        RebuildVisibleRows();
        SelectedGame = GetDisplayGames().FirstOrDefault();
    }

    private void EnsureGameVisible(Game game)
    {
        var displayGames = GetDisplayGames();
        var index = -1;
        for (var i = 0; i < displayGames.Count; i++)
        {
            if (ReferenceEquals(displayGames[i], game) || displayGames[i].Id == game.Id)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return;

        if (index < _materializedGameCount)
            return;

        _loadedGameCount = Math.Min(displayGames.Count, Math.Max(_loadedGameCount, index + 1));
        AppendVisibleGames(_loadedGameCount, _visibleRefreshVersion);
    }

    public async Task OpenGameManagementAsync()
    {
        try
        {
            if (_bulkDeleteWindow is { IsVisible: true })
            {
                _bulkDeleteWindow.Activate();
                return;
            }

            var vm = new GameManagementViewModel(_dbService, _dialogCoordinator);
            await vm.LoadGamesAsync();

            var win = new BulkDeleteWindow
            {
                DataContext = vm,
                ShowInTaskbar = false
            };

            _bulkDeleteWindow = win;
            win.Closed += async (_, _) =>
            {
                _bulkDeleteWindow = null;
                if (!win.HasDeletedGames)
                    return;

                try
                {
                    var games = await _dbService.GetAllGamesAsync();
                    ReplaceGames(games);
                    QueueRecommendationProfileRefresh();
                }
                catch (Exception ex)
                {
                    Perelegans.App.WriteCrashLog(ex);
                    System.Windows.MessageBox.Show(
                        ex.ToString(),
                        TranslationService.Instance["Msg_ErrorTitle"],
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };

            win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            Perelegans.App.WriteCrashLog(ex);
            throw;
        }
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
            AssistantViewModel = null;
            if (IsAssistantPanelVisible)
            {
                EnsureAssistantViewModel();
            }

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
        OpenMetadataForGame(SelectedGame, isSearchEnabled: true);
    }

    [RelayCommand]
    private void EditMetadata()
    {
        if (SelectedGame == null) return;
        OpenMetadataForGame(SelectedGame, isSearchEnabled: false);
    }

    private void OpenMetadataForGame(Game targetGame, bool isSearchEnabled)
    {
        var vm = new MetadataViewModel(targetGame, _httpClient, _dbService, _settingsService, isNewGame: false, isSearchEnabled: isSearchEnabled);
        var win = new MetadataWindow
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow
        };

        if (win.ShowDialog() == true)
        {
            HandleMetadataSaved(targetGame);
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
    private async Task ChangeGameStatus(GameStatus status)
    {
        if (SelectedGame == null || SelectedGame.Status == status)
            return;

        var previousStatus = SelectedGame.Status;
        SelectedGame.Status = status;

        try
        {
            await _dbService.UpdateGameAsync(SelectedGame);
            RefreshStats();
            _processMonitor.UpdateMonitoredGames(Games);
            QueueRecommendationProfileRefresh();

            if (status == GameStatus.Completed && previousStatus != GameStatus.Completed)
                StartAiCompletionNoteInBackground(SelectedGame);
        }
        catch (Exception ex)
        {
            SelectedGame.Status = previousStatus;
            await _dialogCoordinator.ShowMessageAsync(
                this,
                TranslationService.Instance["Msg_ErrorTitle"],
                ex.Message);
        }
    }

    private void StartAiCompletionNoteInBackground(Game game)
    {
        if (game.Id <= 0)
            return;

        var snapshot = new Game
        {
            Id = game.Id,
            Title = game.Title,
            Brand = game.Brand,
            ReleaseDate = game.ReleaseDate,
            Status = game.Status,
            Playtime = game.Playtime,
            Tags = game.Tags
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var aiService = new AiRecommendationService(_httpClient, _settingsService);
                if (!aiService.IsConfigured)
                    return;

                var cacheService = new VndbRecommendationCacheService();
                var cache = await cacheService.LoadAsync();
                var cacheKey = snapshot.Id.ToString(CultureInfo.InvariantCulture);
                if (cache.CompletionNotes.TryGetValue(cacheKey, out var cached) &&
                    !string.IsNullOrWhiteSpace(cached.Note))
                {
                    return;
                }

                var note = await aiService.GenerateCompletionNoteAsync(snapshot);
                if (string.IsNullOrWhiteSpace(note))
                    return;

                cache.CompletionNotes[cacheKey] = new CachedCompletionNote
                {
                    GameId = snapshot.Id,
                    Note = note,
                    CachedAtUtc = DateTimeOffset.UtcNow
                };
                await cacheService.SaveAsync(cache);

                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await _dialogCoordinator.ShowMessageAsync(
                        this,
                        "AI Note",
                        note);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AI completion note error: {ex.Message}");
            }
        });
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
            SelectedGame = null;
            QueueRecommendationProfileRefresh();
        }
    }

    private void QueueRecommendationProfileRefresh()
    {
        var version = Interlocked.Increment(ref _profileRefreshVersion);
        _ = WarmRecommendationProfileAsync(version);
    }

    private async Task WarmRecommendationProfileAsync(int version)
    {
        try
        {
            await Task.Delay(1200);
            if (version != _profileRefreshVersion)
                return;

            var recommendationService = new RecommendationService(
                _dbService,
                _httpClient,
                new VndbRecommendationCacheService());
            await recommendationService.WarmProfileCacheAsync();
        }
        catch (Exception ex)
        {
            Perelegans.App.WriteCrashLog(ex);
        }
    }

}




