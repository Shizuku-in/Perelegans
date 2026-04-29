using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Converters;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class MetadataViewModel : ObservableObject
{
    private readonly VndbService _vndbService;
    private readonly BangumiService _bangumiService;
    private readonly ErogameSpaceService _egsService;
    private readonly DatabaseService _dbService;
    private readonly CoverArtService _coverArtService;
    private readonly bool _isNewGame;
    private readonly string _coverCacheKey;
    private bool _suppressCoverFieldSync;
    private double? _editCoverAspectRatio;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedSource = "VNDB";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<MetadataResult> _searchResults = new();

    [ObservableProperty]
    private MetadataResult? _selectedResult;

    [ObservableProperty]
    private bool _isSearchEnabled = true;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editBrand = string.Empty;

    [ObservableProperty]
    private DateTime? _editReleaseDate;

    [ObservableProperty]
    private GameStatus _editStatus;

    [ObservableProperty]
    private string _editVndbId = string.Empty;

    [ObservableProperty]
    private string _editBangumiId = string.Empty;

    [ObservableProperty]
    private string _editEgsId = string.Empty;

    [ObservableProperty]
    private string _editWebsite = string.Empty;

    [ObservableProperty]
    private string _editCoverImagePath = string.Empty;

    [ObservableProperty]
    private string _editCoverImageUrl = string.Empty;

    [ObservableProperty]
    private string _coverPreviewSource = string.Empty;

    [ObservableProperty]
    private string _coverStatusText = string.Empty;

    [ObservableProperty]
    private string _metadataStatusText = string.Empty;

    [ObservableProperty]
    private string _vndbSourceStatusText = string.Empty;

    [ObservableProperty]
    private string _bangumiSourceStatusText = string.Empty;

    [ObservableProperty]
    private string _egsSourceStatusText = string.Empty;

    [ObservableProperty]
    private string _editTagsText = string.Empty;

    [ObservableProperty]
    private string _editProcessName = string.Empty;

    [ObservableProperty]
    private string _editExecutablePath = string.Empty;

    public Game TargetGame { get; }

    public string[] SourceOptions { get; } = { "VNDB", "Bangumi", "ErogameSpace" };
    public IReadOnlyList<GameStatusOption> StatusOptions { get; } =
    [
        new(GameStatus.Planned, TranslationService.Instance["GameStatus_Planned"]),
        new(GameStatus.Completed, TranslationService.Instance["GameStatus_Completed"]),
        new(GameStatus.Playing, TranslationService.Instance["GameStatus_Playing"]),
        new(GameStatus.Dropped, TranslationService.Instance["GameStatus_Dropped"])
    ];

    public MetadataViewModel(
        Game game,
        HttpClient httpClient,
        DatabaseService dbService,
        bool isNewGame = false,
        bool isSearchEnabled = true)
    {
        TargetGame = game;
        _dbService = dbService;
        _isNewGame = isNewGame;
        _coverArtService = new CoverArtService(httpClient);
        _coverCacheKey = game.Id > 0 ? $"game-{game.Id}" : $"draft-{Guid.NewGuid():N}";
        _vndbService = new VndbService(httpClient);
        _bangumiService = new BangumiService(httpClient);
        _egsService = new ErogameSpaceService(httpClient);
        _isSearchEnabled = isSearchEnabled;

        _searchQuery = game.Title;
        _editTitle = game.Title;
        _editBrand = game.Brand;
        _editReleaseDate = game.ReleaseDate;
        _editStatus = game.Status;
        _editVndbId = game.VndbId ?? string.Empty;
        _editBangumiId = game.BangumiId ?? string.Empty;
        _editEgsId = game.ErogameSpaceId ?? string.Empty;
        _editWebsite = game.OfficialWebsite ?? string.Empty;
        _editCoverImagePath = game.CoverImagePath ?? string.Empty;
        _editCoverImageUrl = game.CoverImageUrl ?? string.Empty;
        _editCoverAspectRatio = game.CoverAspectRatio;
        _editTagsText = TagUtilities.ToMultilineText(TagUtilities.Deserialize(game.Tags));
        _editProcessName = game.ProcessName ?? string.Empty;
        _editExecutablePath = game.ExecutablePath ?? string.Empty;

        RefreshCoverPreview();
        ResetSourceStatuses();
    }

    partial void OnSelectedSourceChanged(string value)
    {
        MetadataStatusText = string.Format(
            TranslationService.Instance["Meta_SourceReady"],
            value);
    }

    partial void OnEditCoverImagePathChanged(string value)
    {
        if (_suppressCoverFieldSync)
        {
            RefreshCoverPreview();
            return;
        }

        var trimmed = value?.Trim() ?? string.Empty;
        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            _suppressCoverFieldSync = true;
            EditCoverImagePath = trimmed;
            _suppressCoverFieldSync = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) && !string.IsNullOrWhiteSpace(EditCoverImageUrl))
        {
            _suppressCoverFieldSync = true;
            EditCoverImageUrl = string.Empty;
            _suppressCoverFieldSync = false;
        }

        _editCoverAspectRatio = CoverArtService.TryReadCoverAspectRatio(trimmed);
        CoverArtImageSourceConverter.InvalidateCache(trimmed);
        CoverStatusText = string.Empty;
        RefreshCoverPreview();
    }

    partial void OnEditCoverImageUrlChanged(string value)
    {
        if (_suppressCoverFieldSync)
        {
            RefreshCoverPreview();
            return;
        }

        var trimmed = value?.Trim() ?? string.Empty;
        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            _suppressCoverFieldSync = true;
            EditCoverImageUrl = trimmed;
            _suppressCoverFieldSync = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(trimmed) && !string.IsNullOrWhiteSpace(EditCoverImagePath))
        {
            _suppressCoverFieldSync = true;
            EditCoverImagePath = string.Empty;
            _suppressCoverFieldSync = false;
        }

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _editCoverAspectRatio = null;
        }

        CoverArtImageSourceConverter.InvalidateCache(trimmed);
        CoverStatusText = string.Empty;
        RefreshCoverPreview();
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        IsSearching = true;
        SearchResults.Clear();
        SetSourceStatus(SelectedSource, TranslationService.Instance["Workflow_Running"]);
        MetadataStatusText = string.Format(
            TranslationService.Instance["Meta_SearchingSource"],
            SelectedSource);

        try
        {
            var results = SelectedSource switch
            {
                "VNDB" => await _vndbService.SearchAsync(SearchQuery),
                "Bangumi" => await _bangumiService.SearchAsync(SearchQuery),
                "ErogameSpace" => await _egsService.SearchAsync(SearchQuery),
                _ => new List<MetadataResult>()
            };

            foreach (var result in results)
            {
                SearchResults.Add(result);
            }

            SetSourceStatus(
                SelectedSource,
                results.Count == 0
                    ? TranslationService.Instance["Meta_SourceNoResults"]
                    : string.Format(TranslationService.Instance["Meta_SourceResultCount"], results.Count));
            MetadataStatusText = string.Format(
                TranslationService.Instance["Meta_SearchComplete"],
                SelectedSource,
                results.Count);
        }
        catch (Exception ex)
        {
            SetSourceStatus(SelectedSource, TranslationService.Instance["Workflow_Failed"]);
            MetadataStatusText = string.Format(
                TranslationService.Instance["Meta_SearchFailed"],
                SelectedSource);
            System.Diagnostics.Debug.WriteLine($"Metadata search error: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void ApplySelected()
    {
        if (SelectedResult == null)
            return;

        MetadataStatusText = string.Format(
            TranslationService.Instance["Meta_AppliedSource"],
            SelectedResult.Source);

        var selectedTitle = !string.IsNullOrWhiteSpace(SelectedResult.OriginalTitle)
            ? SelectedResult.OriginalTitle
            : SelectedResult.Title;

        if (!string.IsNullOrEmpty(selectedTitle))
            EditTitle = selectedTitle;
        if (!string.IsNullOrEmpty(SelectedResult.Brand))
            EditBrand = SelectedResult.Brand;
        if (SelectedResult.ReleaseDate.HasValue)
            EditReleaseDate = SelectedResult.ReleaseDate;

        var mergedTags = TagUtilities.Merge(
            TagUtilities.ParseMultilineText(EditTagsText),
            SelectedResult.Tags);
        EditTagsText = TagUtilities.ToMultilineText(mergedTags);

        switch (SelectedResult.Source)
        {
            case "VNDB":
                EditVndbId = SelectedResult.SourceId;
                break;
            case "Bangumi":
                EditBangumiId = SelectedResult.SourceId;
                break;
            case "ErogameSpace":
                EditEgsId = SelectedResult.SourceId;
                break;
        }

        if (!string.IsNullOrWhiteSpace(SelectedResult.ImageUrl))
        {
            SetCoverFields(
                path: null,
                url: SelectedResult.ImageUrl,
                aspectRatio: null,
                statusText: TranslationService.Instance["Meta_CoverAppliedFromResult"]);
        }
    }

    [RelayCommand]
    private void ImportFromLocal()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All Files (*.*)|*.*",
            Title = TranslationService.Instance["Meta_CoverBrowseTitle"]
        };

        if (dialog.ShowDialog() != true)
            return;

        var importedCover = _coverArtService.ImportLocalCoverToCache(dialog.FileName, _coverCacheKey);
        if (string.IsNullOrWhiteSpace(importedCover?.CachedPath) || !importedCover.AspectRatio.HasValue)
        {
            CoverStatusText = TranslationService.Instance["Meta_CoverInvalidFile"];
            return;
        }

        SetCoverFields(
            path: importedCover.CachedPath,
            url: null,
            aspectRatio: importedCover.AspectRatio,
            statusText: TranslationService.Instance["Meta_CoverSelectedLocal"]);
    }

    [RelayCommand]
    private void ClearCover()
    {
        SetCoverFields(
            path: null,
            url: null,
            aspectRatio: null,
            statusText: TranslationService.Instance["Meta_CoverCleared"]);
    }

    public async Task<IReadOnlyList<CoverCandidate>> LoadCoverCandidatesAsync()
    {
        var title = string.IsNullOrWhiteSpace(EditTitle) ? SearchQuery.Trim() : EditTitle.Trim();
        var bangumiId = NullIfWhiteSpace(EditBangumiId);
        var vndbId = NullIfWhiteSpace(EditVndbId);

        if (string.IsNullOrWhiteSpace(title) && bangumiId == null && vndbId == null)
        {
            CoverStatusText = TranslationService.Instance["Meta_CoverFetchMissingInput"];
            return Array.Empty<CoverCandidate>();
        }

        CoverStatusText = TranslationService.Instance["Meta_CoverPickerLoading"];

        var candidates = await _coverArtService.GetCoverCandidatesAsync(
            title,
            bangumiId,
            vndbId);

        if (candidates.Count == 0)
        {
            CoverStatusText = TranslationService.Instance["Meta_CoverFetchFailed"];
            return Array.Empty<CoverCandidate>();
        }

        await _coverArtService.PopulateCandidatePreviewSourcesAsync(candidates, $"{_coverCacheKey}-picker");

        CoverStatusText = string.Empty;
        return candidates;
    }

    public async Task ApplyCoverCandidateAsync(CoverCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ImageUrl))
        {
            CoverStatusText = TranslationService.Instance["Meta_CoverFetchFailed"];
            return;
        }

        var result = await _coverArtService.CacheCoverFromUrlAsync(candidate.ImageUrl, _coverCacheKey);
        var statusText = !string.IsNullOrWhiteSpace(result?.CachedPath)
            ? TranslationService.Instance["Meta_CoverFetchSuccessCached"]
            : TranslationService.Instance["Meta_CoverAppliedFromAutoFetch"];
        SetCoverFields(
            path: result?.CachedPath,
            url: result?.CoverUrl ?? candidate.ImageUrl,
            aspectRatio: result?.AspectRatio,
            statusText: statusText);
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select Game Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            EditExecutablePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(EditProcessName))
            {
                EditProcessName = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        var coverPath = EditCoverImagePath.Trim();
        var coverUrl = EditCoverImageUrl.Trim();
        var previousCoverDisplaySource = TargetGame.CoverDisplaySource;

        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            if (!File.Exists(coverPath))
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidFile"]);

            var aspectRatio = CoverArtService.TryReadCoverAspectRatio(coverPath);
            if (!aspectRatio.HasValue)
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidFile"]);

            _editCoverAspectRatio = aspectRatio;
        }
        else if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) || !IsSupportedCoverUriScheme(uri))
            {
                throw new InvalidOperationException(TranslationService.Instance["Meta_CoverInvalidUrl"]);
            }

            var cachedCover = await _coverArtService.CacheCoverFromUrlAsync(coverUrl, _coverCacheKey);
            if (cachedCover?.CachedPath is { Length: > 0 } cachedPath)
            {
                coverPath = cachedPath;
                _editCoverAspectRatio = cachedCover.AspectRatio ?? CoverArtService.TryReadCoverAspectRatio(cachedPath);
            }
            else
            {
                _editCoverAspectRatio = null;
            }
        }
        else
        {
            _editCoverAspectRatio = null;
        }

        var normalizedCoverPath = NullIfWhiteSpace(coverPath);
        var normalizedCoverUrl = NullIfWhiteSpace(coverUrl);

        CoverArtImageSourceConverter.InvalidateCache(previousCoverDisplaySource);
        CoverArtImageSourceConverter.InvalidateCache(normalizedCoverPath);
        CoverArtImageSourceConverter.InvalidateCache(normalizedCoverUrl);

        TargetGame.Title = EditTitle;
        TargetGame.Brand = EditBrand;
        TargetGame.ReleaseDate = EditReleaseDate;
        TargetGame.Status = EditStatus;
        TargetGame.VndbId = NullIfWhiteSpace(EditVndbId);
        TargetGame.BangumiId = NullIfWhiteSpace(EditBangumiId);
        TargetGame.ErogameSpaceId = NullIfWhiteSpace(EditEgsId);
        TargetGame.OfficialWebsite = NullIfWhiteSpace(EditWebsite);
        TargetGame.Tags = TagUtilities.Serialize(TagUtilities.ParseMultilineText(EditTagsText));
        TargetGame.ProcessName = EditProcessName;
        TargetGame.ExecutablePath = EditExecutablePath;
        TargetGame.CoverImagePath = normalizedCoverPath;
        TargetGame.CoverImageUrl = normalizedCoverUrl;
        TargetGame.CoverAspectRatio = _editCoverAspectRatio;
        TargetGame.RefreshCoverBindings();

        if (!_isNewGame)
        {
            await _dbService.UpdateGameAsync(TargetGame);
        }
    }

    private void SetCoverFields(string? path, string? url, double? aspectRatio, string statusText)
    {
        var trimmedPath = path?.Trim() ?? string.Empty;
        var trimmedUrl = url?.Trim() ?? string.Empty;

        CoverArtImageSourceConverter.InvalidateCache(CoverPreviewSource);
        CoverArtImageSourceConverter.InvalidateCache(trimmedPath);
        CoverArtImageSourceConverter.InvalidateCache(trimmedUrl);

        _suppressCoverFieldSync = true;
        EditCoverImagePath = trimmedPath;
        EditCoverImageUrl = trimmedUrl;
        _suppressCoverFieldSync = false;

        _editCoverAspectRatio = aspectRatio;
        RefreshCoverPreview(forceNotify: true);
        CoverStatusText = statusText;
    }

    private void ResetSourceStatuses()
    {
        VndbSourceStatusText = TranslationService.Instance["Workflow_Waiting"];
        BangumiSourceStatusText = TranslationService.Instance["Workflow_Waiting"];
        EgsSourceStatusText = TranslationService.Instance["Workflow_Waiting"];
        MetadataStatusText = string.Format(
            TranslationService.Instance["Meta_SourceReady"],
            SelectedSource);
    }

    private void SetSourceStatus(string source, string statusText)
    {
        switch (source)
        {
            case "VNDB":
                VndbSourceStatusText = statusText;
                break;
            case "Bangumi":
                BangumiSourceStatusText = statusText;
                break;
            case "ErogameSpace":
                EgsSourceStatusText = statusText;
                break;
        }
    }

    private void RefreshCoverPreview(bool forceNotify = false)
    {
        var coverPath = EditCoverImagePath.Trim();
        var coverUrl = EditCoverImageUrl.Trim();

        var nextPreviewSource = !string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath)
            ? coverPath
            : coverUrl;

        if (string.Equals(CoverPreviewSource, nextPreviewSource, StringComparison.Ordinal))
        {
            if (forceNotify)
            {
                OnPropertyChanged(nameof(CoverPreviewSource));
            }

            return;
        }

        CoverPreviewSource = nextPreviewSource;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static bool IsSupportedCoverUriScheme(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp ||
               uri.Scheme == Uri.UriSchemeHttps ||
               uri.Scheme == Uri.UriSchemeFile;
    }
}

public sealed class GameStatusOption(GameStatus value, string label)
{
    public GameStatus Value { get; } = value;
    public string Label { get; } = label;
}
