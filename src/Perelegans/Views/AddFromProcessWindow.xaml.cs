using System.Windows;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Services;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class AddFromProcessWindow : MetroWindow
{
    public AddFromProcessWindow()
    {
        InitializeComponent();
    }

    private async void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AddFromProcessViewModel vm && vm.SelectedProcess != null)
        {
            DialogResult = true;
        }
        else
        {
            await this.ShowMessageAsync(TranslationService.Instance["Msg_AppTitle"], TranslationService.Instance["Msg_SelectProcess"]);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
