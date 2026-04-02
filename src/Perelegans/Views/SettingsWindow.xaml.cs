using System;
using System.Windows;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Services;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class SettingsWindow : MetroWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        try
        {
            vm.SaveCommand.Execute(null);
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
