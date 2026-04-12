using System;
using System.Collections.Generic;
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

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        _arrangedRects.Clear();

        if (InternalChildren.Count == 0)
            return new System.Windows.Size(0, 0);

        var panelWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? ColumnWidth
            : availableSize.Width;

        var spacing = Math.Max(0, ItemSpacing);
        var columnWidth = Math.Max(1, ColumnWidth);
        var columns = Math.Max(1, (int)Math.Floor((panelWidth + spacing) / (columnWidth + spacing)));
        var usedWidth = columns * columnWidth + (columns - 1) * spacing;
        var horizontalOffset = Math.Max(0, (panelWidth - usedWidth) / 2d);
        var columnHeights = new double[columns];

        foreach (System.Windows.UIElement child in InternalChildren)
        {
            child.Measure(new System.Windows.Size(columnWidth, double.PositiveInfinity));

            var targetColumn = GetShortestColumnIndex(columnHeights);
            var x = horizontalOffset + targetColumn * (columnWidth + spacing);
            var y = columnHeights[targetColumn];

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

        return new System.Windows.Size(panelWidth, finalHeight);
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
