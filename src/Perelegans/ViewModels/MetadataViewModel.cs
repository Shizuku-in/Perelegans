using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class MetadataViewModel : ObservableObject
{
    private readonly VndbService _vndbService;
    private readonly BangumiService _bangumiService;
    private readonly ErogameSpaceService _egsService;
    private readonly DatabaseService _dbService;

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

    // Editable fields
    [ObservableProperty]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editBrand = string.Empty;

    [ObservableProperty]
    private DateTime? _editReleaseDate;

    [ObservableProperty]
    private string _editVndbId = string.Empty;

    [ObservableProperty]
    private string _editBangumiId = string.Empty;

    [ObservableProperty]
    private string _editEgsId = string.Empty;

    [ObservableProperty]
    private string _editWebsite = string.Empty;

    [ObservableProperty]
    private string _editProcessName = string.Empty;

    [ObservableProperty]
    private string _editExecutablePath = string.Empty;

    public Game TargetGame { get; }

    public string[] SourceOptions { get; } = { "VNDB", "Bangumi", "ErogameSpace" };

    public MetadataViewModel(
        Game game,
        HttpClient httpClient,
        DatabaseService dbService,
        bool isSearchEnabled = true)
    {
        TargetGame = game;
        _dbService = dbService;
        _vndbService = new VndbService(httpClient);
        _bangumiService = new BangumiService(httpClient);
        _egsService = new ErogameSpaceService(httpClient);
        _isSearchEnabled = isSearchEnabled;

        // Pre-fill editable fields from game
        _searchQuery = game.Title;
        _editTitle = game.Title;
        _editBrand = game.Brand;
        _editReleaseDate = game.ReleaseDate;
        _editVndbId = game.VndbId ?? "";
        _editBangumiId = game.BangumiId ?? "";
        _editEgsId = game.ErogameSpaceId ?? "";
        _editWebsite = game.OfficialWebsite ?? "";
        _editProcessName = game.ProcessName ?? "";
        _editExecutablePath = game.ExecutablePath ?? "";
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        SearchResults.Clear();

        try
        {
            var results = SelectedSource switch
            {
                "VNDB" => await _vndbService.SearchAsync(SearchQuery),
                "Bangumi" => await _bangumiService.SearchAsync(SearchQuery),
                "ErogameSpace" => await _egsService.SearchAsync(SearchQuery),
                _ => new List<MetadataResult>()
            };

            foreach (var r in results)
                SearchResults.Add(r);
        }
        catch (Exception ex)
        {
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
        if (SelectedResult == null) return;

        if (!string.IsNullOrEmpty(SelectedResult.Title))
            EditTitle = SelectedResult.Title;
        if (!string.IsNullOrEmpty(SelectedResult.Brand))
            EditBrand = SelectedResult.Brand;
        if (SelectedResult.ReleaseDate.HasValue)
            EditReleaseDate = SelectedResult.ReleaseDate;

        // Fill source-specific ID
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
                EditProcessName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        TargetGame.Title = EditTitle;
        TargetGame.Brand = EditBrand;
        TargetGame.ReleaseDate = EditReleaseDate;
        TargetGame.VndbId = string.IsNullOrWhiteSpace(EditVndbId) ? null : EditVndbId;
        TargetGame.BangumiId = string.IsNullOrWhiteSpace(EditBangumiId) ? null : EditBangumiId;
        TargetGame.ErogameSpaceId = string.IsNullOrWhiteSpace(EditEgsId) ? null : EditEgsId;
        TargetGame.OfficialWebsite = string.IsNullOrWhiteSpace(EditWebsite) ? null : EditWebsite;
        TargetGame.ProcessName = EditProcessName;
        TargetGame.ExecutablePath = EditExecutablePath;

        await _dbService.UpdateGameAsync(TargetGame);
    }
}
