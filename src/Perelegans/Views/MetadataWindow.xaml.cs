using System.Windows;
using System.Windows.Input;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class MetadataWindow : MetroWindow
{
    public MetadataWindow()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MetadataViewModel vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
