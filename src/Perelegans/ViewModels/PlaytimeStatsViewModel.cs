using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Events;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Perelegans.Models;
using Perelegans.Services;
using SkiaSharp;

namespace Perelegans.ViewModels;

public partial class PlaytimeStatsViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private List<Game> _allGames = new();
    private List<PlaySession> _allSessions = new();
    private string? _hoveredPieKey;

    private static readonly SKColor[] PiePalette = new[]
    {
        SKColor.Parse("#F09199"), // 桃色
        SKColor.Parse("#F2A0A1"), // 紅梅色
        SKColor.Parse("#EE827C"), // 甚三紅
        SKColor.Parse("#EB9B6F"), // 深支子
        SKColor.Parse("#F6B894"), // 赤香
        SKColor.Parse("#F5B1AA"), // 珊瑚色
        SKColor.Parse("#EEBBCB"), // 撫子色
        SKColor.Parse("#BC64A4"), // 若紫
        SKColor.Parse("#FDEFF2"), // 薄桜
        SKColor.Parse("#FDDEA5"), // 蜂蜜色
        SKColor.Parse("#E4D2D8"), // 鴇鼠
        SKColor.Parse("#F7B977"), // 杏色
        SKColor.Parse("#E0815E"), // 纁
        SKColor.Parse("#F2C9AC"), // 洗柿
        SKColor.Parse("#A58F86"), // 胡桃染
    };

    private SKColor _chartSliceBorderColor = SKColor.Parse("#252526");

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ObservableCollection<PeriodRow> _periodData = new();

    [ObservableProperty]
    private PeriodRow? _selectedPeriod;

    [ObservableProperty]
    private ISeries[] _pieSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private ObservableCollection<PieLegendItem> _pieLegendItems = new();

    [ObservableProperty]
    private string _chartSubtitle = string.Empty;

    [ObservableProperty]
    private PieLegendItem? _highlightedLegendItem;

    public PlaytimeStatsViewModel(DatabaseService dbService)
    {
        _dbService = dbService;
    }

    public void ApplyChartTheme(ResourceDictionary resources)
    {
        _chartSliceBorderColor = GetBrushColor(resources, "Perelegans.StatsChartSliceBorderBrush", _chartSliceBorderColor);

        if (SelectedPeriod != null || PieSeries.Length > 0)
        {
            RefreshPieChart();
        }
    }

    public void ClearPieHover()
    {
        SetHoveredPieKey(null);
    }

    public async Task InitializeAsync()
    {
        _allGames = await _dbService.GetAllGamesAsync();
        _allSessions = new List<PlaySession>();
        foreach (var g in _allGames)
        {
            var sessions = await _dbService.GetSessionsForGameAsync(g.Id);
            _allSessions.AddRange(sessions);
        }

        RefreshData();
    }

    [RelayCommand]
    private void PieHoverChanged(HoverCommandArgs? args)
    {
        var hoveredKey = args?.NewPoints?.FirstOrDefault()?.Context.Series?.Name;
        SetHoveredPieKey(string.IsNullOrWhiteSpace(hoveredKey) ? null : hoveredKey);
    }

    partial void OnSelectedTabIndexChanged(int value) => RefreshData();

    partial void OnSelectedPeriodChanged(PeriodRow? value)
    {
        _hoveredPieKey = null;
        RefreshPieChart();
    }

    private void RefreshData()
    {
        PeriodData.Clear();
        var now = DateTime.Now;

        var periods = SelectedTabIndex switch
        {
            0 => GetLast7Days(now),
            1 => GetByWeek(now),
            2 => GetByMonth(now),
            3 => GetByYear(now),
            _ => new List<(string Label, DateTime Start, DateTime End)>()
        };

        foreach (var (label, start, end) in periods)
        {
            var sessionsInPeriod = _allSessions
                .Where(s => s.StartTime >= start && s.StartTime < end)
                .ToList();

            var totalTime = TimeSpan.Zero;
            foreach (var session in sessionsInPeriod)
            {
                totalTime += session.Duration;
            }

            PeriodData.Add(new PeriodRow
            {
                Label = label,
                Start = start,
                End = end,
                TotalPlaytime = totalTime,
                SessionCount = sessionsInPeriod.Count
            });
        }

        if (PeriodData.Count > 0)
        {
            SelectedPeriod = PeriodData[0];
        }
        else
        {
            ChartSubtitle = string.Empty;
            HighlightedLegendItem = null;
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
        }
    }

    private void RefreshPieChart()
    {
        if (SelectedPeriod == null)
        {
            ChartSubtitle = string.Empty;
            HighlightedLegendItem = null;
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        ChartSubtitle = SelectedPeriod.Label;

        var slices = _allSessions
            .Where(s => s.StartTime >= SelectedPeriod.Start && s.StartTime < SelectedPeriod.End)
            .GroupBy(s => s.GameId)
            .Select(group =>
            {
                var total = TimeSpan.Zero;
                foreach (var session in group)
                {
                    total += session.Duration;
                }

                var game = _allGames.FirstOrDefault(g => g.Id == group.Key);
                return new PieSliceData(
                    group.Key.ToString(CultureInfo.InvariantCulture),
                    game?.Title ?? $"Game #{group.Key}",
                    total,
                    total.TotalMinutes);
            })
            .Where(slice => slice.Minutes > 0)
            .OrderByDescending(slice => slice.Minutes)
            .ToList();

        if (slices.Count == 0)
        {
            HighlightedLegendItem = null;
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var totalMinutes = slices.Sum(slice => slice.Minutes);
        var series = new List<ISeries>(slices.Count);
        var legendItems = new ObservableCollection<PieLegendItem>();

        for (int i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var color = PiePalette[i % PiePalette.Length];
            var isHighlighted = string.Equals(_hoveredPieKey, slice.Key, StringComparison.Ordinal);

            series.Add(new PieSeries<double>
            {
                Values = new[] { slice.Minutes },
                Name = slice.Key,
                Fill = new SolidColorPaint(color),
                Stroke = new SolidColorPaint(_chartSliceBorderColor) { StrokeThickness = 2 },
                Pushout = 0,
                HoverPushout = 10
            });

            legendItems.Add(new PieLegendItem
            {
                Key = slice.Key,
                Title = slice.Title,
                PlaytimeText = FormatPlaytime(slice.TotalTime),
                PercentageText = totalMinutes <= 0 ? "0%" : $"{slice.Minutes / totalMinutes:P0}",
                SwatchBrush = CreateBrush(color),
                IsHighlighted = isHighlighted
            });
        }

        if (_hoveredPieKey != null && legendItems.All(item => !item.IsHighlighted))
        {
            _hoveredPieKey = null;
        }

        PieLegendItems = legendItems;
        PieSeries = series.ToArray();
        HighlightedLegendItem = PieLegendItems.FirstOrDefault(item => item.IsHighlighted);
    }

    private void SetHoveredPieKey(string? hoveredPieKey)
    {
        if (string.Equals(_hoveredPieKey, hoveredPieKey, StringComparison.Ordinal))
        {
            return;
        }

        _hoveredPieKey = hoveredPieKey;
        UpdateLegendHighlight();
    }

    private void UpdateLegendHighlight()
    {
        PieLegendItem? highlightedItem = null;

        foreach (var item in PieLegendItems)
        {
            var isHighlighted = _hoveredPieKey != null &&
                string.Equals(item.Key, _hoveredPieKey, StringComparison.Ordinal);

            item.IsHighlighted = isHighlighted;
            if (isHighlighted)
            {
                highlightedItem = item;
            }
        }

        HighlightedLegendItem = highlightedItem;
    }

    internal static string FormatPlaytime(TimeSpan totalPlaytime)
    {
        int hours = (int)totalPlaytime.TotalHours;
        int minutes = totalPlaytime.Minutes;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    private static SolidColorBrush CreateBrush(SKColor color)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    private static SKColor GetBrushColor(ResourceDictionary resources, string key, SKColor fallback)
    {
        if (resources[key] is SolidColorBrush brush)
        {
            return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
        }

        return fallback;
    }

    private List<(string Label, DateTime Start, DateTime End)> GetLast7Days(DateTime now)
    {
        var result = new List<(string, DateTime, DateTime)>();
        for (int i = 6; i >= 0; i--)
        {
            var day = now.Date.AddDays(-i);
            result.Add((day.ToString("MM/dd (ddd)"), day, day.AddDays(1)));
        }

        return result;
    }

    private List<(string Label, DateTime Start, DateTime End)> GetByWeek(DateTime now)
    {
        var result = new List<(string, DateTime, DateTime)>();
        for (int i = 11; i >= 0; i--)
        {
            var weekStart = now.Date.AddDays(-(int)now.DayOfWeek - 7 * i);
            var weekEnd = weekStart.AddDays(7);
            result.Add(($"{weekStart:MM/dd}~{weekEnd.AddDays(-1):MM/dd}", weekStart, weekEnd));
        }

        return result;
    }

    private List<(string Label, DateTime Start, DateTime End)> GetByMonth(DateTime now)
    {
        var result = new List<(string, DateTime, DateTime)>();
        for (int i = 11; i >= 0; i--)
        {
            var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
            var monthEnd = monthStart.AddMonths(1);
            result.Add((monthStart.ToString("yyyy/MM"), monthStart, monthEnd));
        }

        return result;
    }

    private List<(string Label, DateTime Start, DateTime End)> GetByYear(DateTime now)
    {
        var result = new List<(string, DateTime, DateTime)>();
        for (int i = 4; i >= 0; i--)
        {
            var yearStart = new DateTime(now.Year - i, 1, 1);
            var yearEnd = yearStart.AddYears(1);
            result.Add((yearStart.ToString("yyyy"), yearStart, yearEnd));
        }

        return result;
    }
}

public class PeriodRow
{
    public string Label { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public TimeSpan TotalPlaytime { get; set; }
    public int SessionCount { get; set; }

    public string PlaytimeText => PlaytimeStatsViewModel.FormatPlaytime(TotalPlaytime);
}

public partial class PieLegendItem : ObservableObject
{
    [ObservableProperty]
    private bool _isHighlighted;

    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string PlaytimeText { get; set; } = "";
    public string PercentageText { get; set; } = "";
    public SolidColorBrush SwatchBrush { get; set; } = new(Colors.Transparent);
}

internal sealed record PieSliceData(string Key, string Title, TimeSpan TotalTime, double Minutes);
