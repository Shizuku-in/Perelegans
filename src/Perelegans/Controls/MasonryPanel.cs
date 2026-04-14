using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Perelegans.Controls;

public class MasonryPanel : System.Windows.Controls.Panel
{
    public static readonly System.Windows.DependencyProperty ColumnWidthProperty =
        System.Windows.DependencyProperty.Register(
            nameof(ColumnWidth),
            typeof(double),
            typeof(MasonryPanel),
            new System.Windows.FrameworkPropertyMetadata(
                240d,
                System.Windows.FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly System.Windows.DependencyProperty ItemSpacingProperty =
        System.Windows.DependencyProperty.Register(
            nameof(ItemSpacing),
            typeof(double),
            typeof(MasonryPanel),
            new System.Windows.FrameworkPropertyMetadata(
                16d,
                System.Windows.FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty =
        DependencyProperty.Register(
            nameof(Padding),
            typeof(Thickness),
            typeof(MasonryPanel),
            new FrameworkPropertyMetadata(
                new Thickness(0),
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly List<System.Windows.Rect> _arrangedRects = new();

    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        _arrangedRects.Clear();

        if (InternalChildren.Count == 0)
            return new System.Windows.Size(0, 0);

        var padding = Padding;
        var leftInset = Math.Max(0, padding.Left);
        var topInset = Math.Max(0, padding.Top);
        var rightInset = Math.Max(0, padding.Right);
        var bottomInset = Math.Max(0, padding.Bottom);

        var contentWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? ColumnWidth
            : Math.Max(1, availableSize.Width - leftInset - rightInset);

        var spacing = Math.Max(0, ItemSpacing);
        var minimumColumnWidth = Math.Max(1, ColumnWidth);
        var columns = Math.Max(1, (int)Math.Floor((contentWidth + spacing) / (minimumColumnWidth + spacing)));
        var totalSpacing = Math.Max(0, columns - 1) * spacing;
        var columnWidth = Math.Max(1, (contentWidth - totalSpacing) / columns);
        var columnHeights = new double[columns];

        foreach (System.Windows.UIElement child in InternalChildren)
        {
            child.Measure(new System.Windows.Size(columnWidth, double.PositiveInfinity));

            var targetColumn = GetShortestColumnIndex(columnHeights);
            var x = leftInset + targetColumn * (columnWidth + spacing);
            var y = topInset + columnHeights[targetColumn];

            _arrangedRects.Add(new System.Windows.Rect(
                new System.Windows.Point(x, y),
                new System.Windows.Size(columnWidth, child.DesiredSize.Height)));
            columnHeights[targetColumn] += child.DesiredSize.Height + spacing;
        }

        var finalHeight = 0d;
        foreach (var height in columnHeights)
        {
            finalHeight = Math.Max(finalHeight, height);
        }

        if (finalHeight > 0)
            finalHeight -= spacing;

        var desiredWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? leftInset + contentWidth + rightInset
            : availableSize.Width;
        var desiredHeight = topInset + finalHeight + bottomInset;

        return new System.Windows.Size(desiredWidth, desiredHeight);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var rect = i < _arrangedRects.Count
                ? _arrangedRects[i]
                : System.Windows.Rect.Empty;

            InternalChildren[i].Arrange(rect);
        }

        return finalSize;
    }

    private static int GetShortestColumnIndex(IReadOnlyList<double> columnHeights)
    {
        var shortestIndex = 0;
        var shortestHeight = columnHeights[0];

        for (var i = 1; i < columnHeights.Count; i++)
        {
            if (columnHeights[i] >= shortestHeight)
                continue;

            shortestHeight = columnHeights[i];
            shortestIndex = i;
        }

        return shortestIndex;
    }
}
