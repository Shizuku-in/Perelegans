using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Models;
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
            
            if (!string.IsNullOrWhiteSpace(vm.BangumiPushStatusText) && 
                !vm.BangumiPushStatusText.Contains("成功") &&
                !vm.BangumiPushStatusText.Contains("已跳过"))
            {
                await this.ShowMessageAsync("Bangumi 推送提示", vm.BangumiPushStatusText);
            }
            
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

    private async void AutoFetchCover_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MetadataViewModel vm)
        {
            return;
        }

        try
        {
            IReadOnlyList<CoverCandidate> candidates = await vm.LoadCoverCandidatesAsync();
            if (candidates.Count == 0)
            {
                return;
            }

            var pickerVm = new CoverPickerViewModel(candidates);
            var pickerWindow = new CoverPickerWindow
            {
                DataContext = pickerVm,
                Owner = this
            };

            if (pickerWindow.ShowDialog() == true && pickerVm.SelectedCandidate != null)
            {
                await vm.ApplyCoverCandidateAsync(pickerVm.SelectedCandidate);
            }
        }
        catch (Exception ex)
        {
            await this.ShowMessageAsync(TranslationService.Instance["Msg_ErrorTitle"], ex.Message);
        }
    }
}
