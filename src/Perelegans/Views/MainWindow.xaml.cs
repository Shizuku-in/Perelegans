using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Perelegans.Models;
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

        if (DataContext is MainViewModel vm)
        {
            vm.RefreshUi();
        }
    }

    private void GameCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not Game game || DataContext is not MainViewModel vm)
        {
            return;
        }

        item.IsSelected = true;
        vm.SelectedGame = game;
    }

    private void GameCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBulkDeleteMode)
        {
            return;
        }

        if (sender is not ListBoxItem item || item.DataContext is not Game game)
        {
            return;
        }

        vm.SelectedGame = game;
        if (vm.StartGameCommand.CanExecute(null))
        {
            vm.StartGameCommand.Execute(null);
        }
    }
}
