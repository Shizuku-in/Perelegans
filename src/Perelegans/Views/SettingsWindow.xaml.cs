using System.Windows;
using MahApps.Metro.Controls;

namespace Perelegans.Views;

public partial class SettingsWindow : MetroWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
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
