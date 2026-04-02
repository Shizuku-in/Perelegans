using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
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

    // Pink gradient palette for pie chart
    private static readonly SKColor[] PinkPalette = new[]
    {
        SKColor.Parse("#E91E78"),
        SKColor.Parse("#F06292"),
        SKColor.Parse("#F48FB1"),
        SKColor.Parse("#F8BBD0"),
        SKColor.Parse("#FCE4EC"),
        SKColor.Parse("#EC407A"),
        SKColor.Parse("#D81B60"),
        SKColor.Parse("#C2185B"),
        SKColor.Parse("#AD1457"),
        SKColor.Parse("#880E4F")
    };

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ObservableCollection<PeriodRow> _periodData = new();

    [ObservableProperty]
    private PeriodRow? _selectedPeriod;

    [ObservableProperty]
    private ISeries[] _pieSeries = Array.Empty<ISeries>();

    public PlaytimeStatsViewModel(DatabaseService dbService)
    {
        _dbService = dbService;
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
            PieSeries = Array.Empty<ISeries>();
    }

    private void RefreshPieChart()
    {
        if (SelectedPeriod == null)
        {
            PieSeries = Array.Empty<ISeries>();
            return;
        }

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
                    Minutes = total.TotalMinutes
                };
            })
            .Where(x => x.Minutes > 0)
            .OrderByDescending(x => x.Minutes)
            .ToList();

        if (sessionsInPeriod.Count == 0)
        {
            PieSeries = Array.Empty<ISeries>();
            return;
        }

        var series = new List<ISeries>();
        for (int i = 0; i < sessionsInPeriod.Count; i++)
        {
            var item = sessionsInPeriod[i];
            var color = PinkPalette[i % PinkPalette.Length];
            series.Add(new PieSeries<double>
            {
                Values = new[] { item.Minutes },
                Name = item.Title,
                Fill = new SolidColorPaint(color),
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 11
            });
        }
        PieSeries = series.ToArray();
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
            int h = (int)TotalPlaytime.TotalHours;
            int m = TotalPlaytime.Minutes;
            return h > 0 ? $"{h}h {m}m" : $"{m}m";
        }
    }
}
