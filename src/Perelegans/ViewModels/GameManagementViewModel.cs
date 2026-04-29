using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class GameManagementViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly IDialogCoordinator _dialogCoordinator;

    [ObservableProperty]
    private ObservableCollection<ManageableGame> _games = new();

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        RefreshFilter();
    }

    public ICollectionView GamesView { get; private set; }

    public GameManagementViewModel(DatabaseService dbService, IDialogCoordinator dialogCoordinator)
    {
        _dbService = dbService;
        _dialogCoordinator = dialogCoordinator;

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
    }

    public async Task LoadGamesAsync()
    {
        var allGames = await _dbService.GetAllGamesAsync();
        Games.Clear();
        foreach (var game in allGames)
        {
            var manageableGame = new ManageableGame(game);
            manageableGame.PropertyChanged += OnManageableGamePropertyChanged;
            Games.Add(manageableGame);
        }

        Games.CollectionChanged -= OnGamesCollectionChanged;
        Games.CollectionChanged += OnGamesCollectionChanged;

        SelectedCount = Games.Count(g => g.IsSelected);
        OnPropertyChanged(nameof(TotalCount));
        GamesView.Refresh();
    }

    private void OnGamesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TotalCount));
        if (e.OldItems != null)
        {
            foreach (ManageableGame game in e.OldItems)
                game.PropertyChanged -= OnManageableGamePropertyChanged;
        }
    }

    private void OnManageableGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManageableGame.IsSelected))
            SelectedCount = Games.Count(g => g.IsSelected);
    }

    public int TotalCount => Games.Count;

    private bool FilterGame(object item)
    {
        if (item is not ManageableGame game) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.ToLowerInvariant();
        return game.Title.ToLowerInvariant().Contains(search)
            || game.Brand.ToLowerInvariant().Contains(search);
    }

    public void RefreshFilter()
    {
        GamesView.Refresh();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var g in Games)
            g.IsSelected = true;
        SelectedCount = Games.Count;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var g in Games)
            g.IsSelected = false;
        SelectedCount = 0;
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var g in Games)
            g.IsSelected = !g.IsSelected;
        SelectedCount = Games.Count(g => g.IsSelected);
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        await DeleteSelectedGamesAsync();
    }

    public async Task<bool> DeleteSelectedGamesAsync()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialogCoordinator.ShowMessageAsync(this,
                TranslationService.Instance["Msg_AppTitle"],
                TranslationService.Instance["Msg_NoSelection"]);
            return false;
        }

        var result = await _dialogCoordinator.ShowMessageAsync(this,
            TranslationService.Instance["Msg_DeleteConfirmTitle"],
            string.Format(TranslationService.Instance["Msg_DeleteSelectedConfirmText"], selected.Count),
            MessageDialogStyle.AffirmativeAndNegative);

        if (result != MessageDialogResult.Affirmative)
            return false;

        foreach (var g in selected)
        {
            await _dbService.DeleteGameAsync(g.Id);
            Games.Remove(g);
        }

        SelectedCount = Games.Count(g => g.IsSelected);
        OnPropertyChanged(nameof(TotalCount));
        return true;
    }
}

public partial class ManageableGame : ObservableObject
{
    private readonly Game _game;

    public ManageableGame(Game game)
    {
        _game = game;
    }

    [ObservableProperty]
    private bool _isSelected;

    public int Id => _game.Id;
    public string Title => _game.Title;
    public string Brand => _game.Brand ?? string.Empty;
    public DateTime? ReleaseDate => _game.ReleaseDate;
    public TimeSpan Playtime => _game.Playtime;
    public DateTime? CreatedDate => _game.CreatedDate;
    public DateTime? AccessedDate => _game.AccessedDate;
    public GameStatus Status => _game.Status;
    public string VndbId => _game.VndbId ?? string.Empty;
}
