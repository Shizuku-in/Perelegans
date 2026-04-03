using MahApps.Metro.Controls;
using Perelegans.ViewModels;
using Application = System.Windows.Application;

namespace Perelegans.Views;

public partial class PlaytimeStatsWindow : MetroWindow
{
    public PlaytimeStatsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyChartTheme();
        Activated += (_, _) => ApplyChartTheme();
    }

    private void ApplyChartTheme()
    {
        if (DataContext is PlaytimeStatsViewModel viewModel)
        {
            viewModel.ApplyChartTheme(Application.Current.Resources);
        }
    }
}
