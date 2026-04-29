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

        try
        {
            if (vm.TotalCount == 0)
            {
                await vm.LoadGamesAsync();
            }
        }
        catch
        {
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
