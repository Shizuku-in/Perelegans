using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class CoverPickerWindow : MetroWindow
{
    public CoverPickerWindow()
    {
        InitializeComponent();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        if (DataContext is not CoverPickerViewModel vm || vm.SelectedCandidate == null)
            return;

        DialogResult = true;
        Close();
    }
}
