using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class BatchMetadataWindow : MetroWindow
{
    private bool _loadedOnce;

    public BatchMetadataWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce || DataContext is not BatchMetadataViewModel vm)
            return;

        _loadedOnce = true;
        await vm.LoadGamesAsync();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
