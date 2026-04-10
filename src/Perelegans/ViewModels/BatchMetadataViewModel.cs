using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class BatchMetadataViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly VndbService _vndbService;
    private readonly BangumiService _bangumiService;
    private readonly ErogameSpaceService _egsService;
    private readonly IDialogCoordinator _dialogCoordinator;

    [ObservableProperty]
    private ObservableCollection<GameMetadataEntry> _entries = new();

    [ObservableProperty]
    private GameMetadataEntry? _selectedEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchProgress = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MetadataResult> _searchResults = new();

    [ObservableProperty]
    private MetadataResult? _selectedResult;

    public ICollectionView EntriesView { get; }

    public BatchMetadataViewModel(DatabaseService dbService, HttpClient httpClient, IDialogCoordinator dialogCoordinator)
    {
        _dbService = dbService;
        _vndbService = new VndbService(httpClient);
        _bangumiService = new BangumiService(httpClient);
        _egsService = new ErogameSpaceService(httpClient);
        _dialogCoordinator = dialogCoordinator;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntries;
    }

    private bool FilterEntries(object item)
    {
        if (item is not GameMetadataEntry entry) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.ToLowerInvariant();
        return entry.Title.ToLowerInvariant().Contains(search)
            || entry.Brand.ToLowerInvariant().Contains(search)
            || entry.StatusText.ToLowerInvariant().Contains(search);
    }

    partial void OnSearchTextChanged(string value)
    {
        EntriesView.Refresh();
    }

    public async Task LoadGamesAsync()
    {
        var allGames = await _dbService.GetAllGamesAsync();
        Entries.Clear();
        foreach (var game in allGames)
        {
            Entries.Add(new GameMetadataEntry(game));
        }
        EntriesView.Refresh();
    }

    [RelayCommand]
    private async Task SearchAllAsync()
    {
        if (SelectedEntry == null) return;

        var entry = SelectedEntry;
        var query = entry.Title;
        if (string.IsNullOrWhiteSpace(query)) return;

        IsSearching = true;
        SearchProgress = $"Searching \"{query}\"...";
        SearchResults.Clear();
        SelectedResult = null;

        try
        {
            // Search all 3 sources concurrently
            var vndbTask = SafeSearch("VNDB", _vndbService.SearchAsync(query));
            var bangumiTask = SafeSearch("Bangumi", _bangumiService.SearchAsync(query));
            var egsTask = SafeSearch("ErogameSpace", _egsService.SearchAsync(query));

            await Task.WhenAll(vndbTask, bangumiTask, egsTask);

            var allResults = vndbTask.Result.Concat(bangumiTask.Result).Concat(egsTask.Result).ToList();
            foreach (var r in allResults)
            {
                SearchResults.Add(r);
            }

            SearchProgress = allResults.Count > 0
                ? $"Found {allResults.Count} results across sources."
                : "No results found.";
        }
        catch (Exception ex)
        {
            SearchProgress = $"Search error: {ex.Message}";
        }

        IsSearching = false;
    }

    private static async Task<List<MetadataResult>> SafeSearch(string source, Task<List<MetadataResult>> task)
    {
        try
        {
            var results = await task;
            System.Diagnostics.Debug.WriteLine($"{source} returned {results.Count} results.");
            return results;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{source} search error: {ex.Message}");
            return new List<MetadataResult>();
        }
    }

    [RelayCommand]
    private void ApplySelectedResult()
    {
        if (SelectedEntry == null || SelectedResult == null) return;

        var entry = SelectedEntry;
        var result = SelectedResult;

        var titleToApply = !string.IsNullOrWhiteSpace(result.OriginalTitle)
            ? result.OriginalTitle : result.Title;

        entry.Title = titleToApply;
        entry.Brand = result.Brand;
        entry.ReleaseDate = result.ReleaseDate;

        // Merge tags
        var existingTags = ParseTags(entry.TagsText);
        var merged = TagUtilities.Merge(existingTags, result.Tags);
        entry.TagsText = string.Join(Environment.NewLine, merged);

        // Fill source-specific IDs
        if (result.Source == "VNDB" && string.IsNullOrWhiteSpace(entry.VndbId))
            entry.VndbId = result.SourceId;
        if (result.Source == "Bangumi" && string.IsNullOrWhiteSpace(entry.BangumiId))
            entry.BangumiId = result.SourceId;
        if (result.Source == "ErogameSpace" && string.IsNullOrWhiteSpace(entry.EgsId))
            entry.EgsId = result.SourceId;

        entry.RefreshCompleteness();
        SearchProgress = $"Applied {result.Source} result.";
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        var changed = Entries.Where(e => e.IsDirty).ToList();
        if (changed.Count == 0)
        {
            await _dialogCoordinator.ShowMessageAsync(this,
                TranslationService.Instance["Msg_AppTitle"],
                "No changes to save.");
            return;
        }

        var result = await _dialogCoordinator.ShowMessageAsync(this,
            TranslationService.Instance["Msg_AppTitle"],
            $"Save metadata for {changed.Count} game(s)?",
            MessageDialogStyle.AffirmativeAndNegative);

        if (result != MessageDialogResult.Affirmative) return;

        int saved = 0;
        foreach (var entry in changed)
        {
            entry.ApplyToGame();
            await _dbService.UpdateGameAsync(entry.Game);
            entry.IsDirty = false;
            saved++;
        }

        await _dialogCoordinator.ShowMessageAsync(this,
            TranslationService.Instance["Msg_AppTitle"],
            $"Saved {saved} game(s).");

        // Reload to refresh the collection
        await LoadGamesAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchResults.Clear();
        SelectedResult = null;
        SearchProgress = string.Empty;
    }

    private static List<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }
}

public partial class GameMetadataEntry : ObservableObject
{
    private readonly Game _game;

    public GameMetadataEntry(Game game)
    {
        _game = game;
        Title = game.Title;
        Brand = game.Brand ?? string.Empty;
        ReleaseDate = game.ReleaseDate;
        VndbId = game.VndbId ?? string.Empty;
        BangumiId = game.BangumiId ?? string.Empty;
        EgsId = game.ErogameSpaceId ?? string.Empty;
        Website = game.OfficialWebsite ?? string.Empty;
        TagsText = ParseTagsText(game.Tags);
        StatusText = GetStatusText(game.Status);
        RefreshCompleteness();
    }

    private static string ParseTagsText(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return string.Empty;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(tags);
            return list != null ? string.Join(Environment.NewLine, list) : string.Empty;
        }
        catch
        {
            return tags;
        }
    }

    private static string GetStatusText(GameStatus status) => status switch
    {
        GameStatus.Playing => TranslationService.Instance["GameStatus_Playing"],
        GameStatus.Dropped => TranslationService.Instance["GameStatus_Dropped"],
        GameStatus.Completed => TranslationService.Instance["GameStatus_Completed"],
        GameStatus.Planned => TranslationService.Instance["GameStatus_Planned"],
        _ => string.Empty
    };

    public Game Game => _game;
    public int Id => _game.Id;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _brand;
    [ObservableProperty] private DateTime? _releaseDate;
    [ObservableProperty] private string _vndbId;
    [ObservableProperty] private string _bangumiId;
    [ObservableProperty] private string _egsId;
    [ObservableProperty] private string _website;
    [ObservableProperty] private string _tagsText;
    [ObservableProperty] private string _statusText;

    [ObservableProperty] private int _completenessScore;
    [ObservableProperty] private bool _isDirty;

    partial void OnTitleChanged(string value) => IsDirty = true;
    partial void OnBrandChanged(string value) => IsDirty = true;
    partial void OnReleaseDateChanged(DateTime? value) => IsDirty = true;
    partial void OnVndbIdChanged(string value) => IsDirty = true;
    partial void OnBangumiIdChanged(string value) => IsDirty = true;
    partial void OnEgsIdChanged(string value) => IsDirty = true;
    partial void OnWebsiteChanged(string value) => IsDirty = true;
    partial void OnTagsTextChanged(string value) => IsDirty = true;

    public void RefreshCompleteness()
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(Title)) score += 15;
        if (!string.IsNullOrWhiteSpace(Brand)) score += 15;
        if (ReleaseDate.HasValue) score += 15;
        if (!string.IsNullOrWhiteSpace(VndbId)) score += 15;
        if (!string.IsNullOrWhiteSpace(BangumiId)) score += 15;
        if (!string.IsNullOrWhiteSpace(EgsId)) score += 10;
        if (!string.IsNullOrWhiteSpace(Website)) score += 10;
        var tagCount = ParseTagCount(TagsText);
        if (tagCount > 0) score += Math.Min(5, tagCount);

        CompletenessScore = score;
        OnPropertyChanged(nameof(CompletenessBarColor));
    }

    public string CompletenessBarColor => CompletenessScore switch
    {
        >= 90 => "#4CAF50",
        >= 60 => "#FF9800",
        >= 30 => "#FFC107",
        _ => "#F44336"
    };

    public void ApplyToGame()
    {
        _game.Title = Title;
        _game.Brand = Brand;
        _game.ReleaseDate = ReleaseDate;
        _game.VndbId = string.IsNullOrWhiteSpace(VndbId) ? null : VndbId;
        _game.BangumiId = string.IsNullOrWhiteSpace(BangumiId) ? null : BangumiId;
        _game.ErogameSpaceId = string.IsNullOrWhiteSpace(EgsId) ? null : EgsId;
        _game.OfficialWebsite = string.IsNullOrWhiteSpace(Website) ? null : Website;
        _game.Tags = SerializeTags(TagsText);
    }

    private static string? SerializeTags(string? text)
    {
        var tags = ParseTags(text);
        return tags.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(tags) : null;
    }

    private static List<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    private static int ParseTagCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(t => !string.IsNullOrWhiteSpace(t));
    }
}
