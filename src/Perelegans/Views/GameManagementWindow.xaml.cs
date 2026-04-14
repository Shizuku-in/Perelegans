using System.Windows;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class GameManagementWindow : MetroWindow
{
    private bool _loadedOnce;

    public GameManagementWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce || DataContext is not GameManagementViewModel vm)
            return;

        _loadedOnce = true;
        await vm.LoadGamesAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
