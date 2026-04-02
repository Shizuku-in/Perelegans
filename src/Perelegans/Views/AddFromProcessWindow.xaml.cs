using System.Windows;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class AddFromProcessWindow : MetroWindow
{
    public AddFromProcessWindow()
    {
        InitializeComponent();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AddFromProcessViewModel vm && vm.SelectedProcess != null)
        {
            DialogResult = true;
        }
        else
        {
            MessageBox.Show("请先选择一个进程。", "Perelegans", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
