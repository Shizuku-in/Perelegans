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

        var selectedCount = vm.GetSelectedGameCount();
        if (selectedCount == 0)
        {
            System.Windows.MessageBox.Show(
                Perelegans.Services.TranslationService.Instance["Msg_NoSelection"],
                Perelegans.Services.TranslationService.Instance["Msg_AppTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            string.Format(Perelegans.Services.TranslationService.Instance["Msg_DeleteSelectedConfirmText"], selectedCount),
            Perelegans.Services.TranslationService.Instance["Msg_DeleteConfirmTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await vm.DeleteSelectedGamesWithoutPromptAsync();
            HasDeletedGames = true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
            System.Windows.MessageBox.Show(
                ex.ToString(),
                Perelegans.Services.TranslationService.Instance["Msg_ErrorTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
