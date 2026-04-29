using System.Windows;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class BulkDeleteWindow : Window
{
    public bool HasDeletedGames { get; private set; }

    public BulkDeleteWindow()
    {
        InitializeComponent();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GameManagementViewModel vm)
            return;

        var deleted = await vm.DeleteSelectedGamesAsync();
        if (deleted)
            HasDeletedGames = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
