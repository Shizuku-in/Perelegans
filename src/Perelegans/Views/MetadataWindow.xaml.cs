using System;
using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Services;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class MetadataWindow : MetroWindow
{
    public MetadataWindow()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MetadataViewModel vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MetadataViewModel vm)
        {
            return;
        }

        try
        {
            await vm.SaveCommand.ExecuteAsync(null);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            await this.ShowMessageAsync(TranslationService.Instance["Msg_ErrorTitle"], ex.Message);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
