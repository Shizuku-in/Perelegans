using System.Windows;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class RecommendationWindow : MetroWindow
{
    private bool _loadedOnce;

    public RecommendationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce || DataContext is not RecommendationViewModel vm)
            return;

        _loadedOnce = true;
        await vm.RefreshAsync();
    }
}
