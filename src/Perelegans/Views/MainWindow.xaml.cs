using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        SizeChanged += OnWindowSizeChanged;
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

        RefreshPageSize();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        SizeChanged -= OnWindowSizeChanged;
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

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshPageSize();
    }

    private void RefreshPageSize()
    {
        if (DataContext is not MainViewModel vm || GameCards.ActualWidth <= 0 || GameCards.ActualHeight <= 0)
        {
            return;
        }

        vm.UpdatePageSize(GameCards.ActualWidth, GameCards.ActualHeight);
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
        if (DataContext is not MainViewModel vm)
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

    private void GameCards_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(listBox, source) is ListBoxItem)
        {
            return;
        }

        listBox.SelectedItem = null;
        listBox.UnselectAll();
        vm.SelectedGame = null;
    }

    private void GameCards_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || DataContext is not MainViewModel vm || !vm.CanLoadMoreGames)
        {
            return;
        }

        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 180)
        {
            return;
        }

        vm.LoadMoreGames();
    }

    private void CardRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(GameCards);
        if (scrollViewer == null)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void StatusBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.DataContext is not Game game || DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.SelectedGame = game;
        if (button.ContextMenu == null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }
}
