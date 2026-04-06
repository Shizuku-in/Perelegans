using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;

namespace Perelegans.Views;

public partial class MainWindow : MetroWindow
{
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(1)
    };

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnClosed;
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    // ---- Title Bar Drag ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            DragMove();
        }
    }

    // ---- Title Bar Buttons ----
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OpenSettingsCommand.Execute(null);
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeIcon.Kind = MahApps.Metro.IconPacks.PackIconMaterialKind.WindowMaximize;
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeIcon.Kind = MahApps.Metro.IconPacks.PackIconMaterialKind.WindowRestore;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        GameDataGrid.Items.Refresh();

        if (DataContext is MainViewModel vm)
        {
            vm.RefreshUi();
        }
    }
}
