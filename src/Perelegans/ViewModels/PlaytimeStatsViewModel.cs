using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Perelegans.Models;
using Perelegans.Services;
using SkiaSharp;
using System.Windows.Media;

namespace Perelegans.ViewModels;

public partial class PlaytimeStatsViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private List<Game> _allGames = new();
    private List<PlaySession> _allSessions = new();

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

    partial void OnSelectedTabIndexChanged(int value) => RefreshData();
    partial void OnSelectedPeriodChanged(PeriodRow? value) => RefreshPieChart();

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
            foreach (var s in sessionsInPeriod)
                totalTime += s.Duration;

            PeriodData.Add(new PeriodRow
            {
                Label = label,
                Start = start,
                End = end,
                TotalPlaytime = totalTime,
                SessionCount = sessionsInPeriod.Count
            });
        }

        // Auto-select first row
        if (PeriodData.Count > 0)
            SelectedPeriod = PeriodData[0];
        else
        {
            ChartSubtitle = string.Empty;
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
        }
    }

    private void RefreshPieChart()
    {
        if (SelectedPeriod == null)
        {
            ChartSubtitle = string.Empty;
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        ChartSubtitle = SelectedPeriod.Label;

        var sessionsInPeriod = _allSessions
            .Where(s => s.StartTime >= SelectedPeriod.Start && s.StartTime < SelectedPeriod.End)
            .GroupBy(s => s.GameId)
            .Select(g =>
            {
                var total = TimeSpan.Zero;
                foreach (var s in g)
                    total += s.Duration;
                var game = _allGames.FirstOrDefault(ga => ga.Id == g.Key);
                return new
                {
                    Title = game?.Title ?? $"Game #{g.Key}",
                    TotalTime = total,
                    Minutes = total.TotalMinutes
                };
            })
            .Where(x => x.Minutes > 0)
            .OrderByDescending(x => x.Minutes)
            .ToList();

        if (sessionsInPeriod.Count == 0)
        {
            PieLegendItems = new ObservableCollection<PieLegendItem>();
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var totalMinutes = sessionsInPeriod.Sum(x => x.Minutes);
        var series = new List<ISeries>();
        var legendItems = new ObservableCollection<PieLegendItem>();

        for (int i = 0; i < sessionsInPeriod.Count; i++)
        {
            var item = sessionsInPeriod[i];
            var color = PiePalette[i % PiePalette.Length];
            series.Add(new PieSeries<double>
            {
                Values = new[] { item.Minutes },
                Name = item.Title,
                Fill = new SolidColorPaint(color),
                Stroke = new SolidColorPaint(_chartSliceBorderColor) { StrokeThickness = 2 },
                Pushout = 0,
                HoverPushout = 0
            });

            legendItems.Add(new PieLegendItem
            {
                Title = item.Title,
                PlaytimeText = FormatPlaytime(item.TotalTime),
                PercentageText = totalMinutes <= 0 ? "0%" : $"{item.Minutes / totalMinutes:P0}",
                SwatchBrush = CreateBrush(color)
            });
        }

        PieLegendItems = legendItems;
        PieSeries = series.ToArray();
    }

    internal static string FormatPlaytime(TimeSpan totalPlaytime)
    {
        int h = (int)totalPlaytime.TotalHours;
        int m = totalPlaytime.Minutes;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
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

    // ---- Period generation helpers ----

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

/// <summary>
/// Row model for the period data table.
/// </summary>
public class PeriodRow
{
    public string Label { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public TimeSpan TotalPlaytime { get; set; }
    public int SessionCount { get; set; }

    public string PlaytimeText
    {
        get
        {
            return PlaytimeStatsViewModel.FormatPlaytime(TotalPlaytime);
        }
    }
}

public class PieLegendItem
{
    public string Title { get; set; } = "";
    public string PlaytimeText { get; set; } = "";
    public string PercentageText { get; set; } = "";
    public SolidColorBrush SwatchBrush { get; set; } = new(System.Windows.Media.Colors.Transparent);
}
