using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using Perelegans.ViewModels;
using Application = System.Windows.Application;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Perelegans.Views;

public partial class PlaytimeStatsWindow : MetroWindow
{
    private const double LegendScrollPadding = 8;

    public PlaytimeStatsWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => ApplyChartTheme();
        Activated += (_, _) => ApplyChartTheme();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PlaytimeStatsViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is PlaytimeStatsViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyChartTheme();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaytimeStatsViewModel.HighlightedLegendItem))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (DataContext is PlaytimeStatsViewModel viewModel)
                    {
                        ScrollHighlightedLegendIntoView(viewModel.HighlightedLegendItem);
                    }
                }));
        }
    }

    private void ApplyChartTheme()
    {
        if (DataContext is PlaytimeStatsViewModel viewModel)
        {
            viewModel.ApplyChartTheme(Application.Current.Resources);
        }
    }

    private void PieStatsChart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is PlaytimeStatsViewModel viewModel)
        {
            viewModel.ClearPieHover();
        }
    }

    private void ScrollHighlightedLegendIntoView(PieLegendItem? item)
    {
        if (item == null)
        {
            return;
        }

        LegendItemsControl.UpdateLayout();

        if (LegendItemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
        {
            return;
        }

        if (LegendScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var top = container.TransformToAncestor(LegendScrollViewer).Transform(new System.Windows.Point(0, 0)).Y;
        var bottom = top + container.ActualHeight;
        var targetOffset = LegendScrollViewer.VerticalOffset;

        if (top < LegendScrollPadding)
        {
            targetOffset += top - LegendScrollPadding;
        }
        else if (bottom > LegendScrollViewer.ViewportHeight - LegendScrollPadding)
        {
            targetOffset += bottom - LegendScrollViewer.ViewportHeight + LegendScrollPadding;
        }
        else
        {
            return;
        }

        targetOffset = Math.Max(0, Math.Min(targetOffset, LegendScrollViewer.ScrollableHeight));
        ScrollAnimationHelper.AnimateVerticalOffset(LegendScrollViewer, targetOffset);
    }
}

internal static class ScrollAnimationHelper
{
    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(ScrollAnimationHelper),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public static void AnimateVerticalOffset(ScrollViewer scrollViewer, double targetOffset)
    {
        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
        scrollViewer.SetValue(AnimatedVerticalOffsetProperty, scrollViewer.VerticalOffset);

        var animation = new DoubleAnimation(scrollViewer.VerticalOffset, targetOffset, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer && e.NewValue is double offset)
        {
            scrollViewer.ScrollToVerticalOffset(offset);
        }
    }
}
