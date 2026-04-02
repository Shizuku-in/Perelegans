using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class GamePlayLogViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;

    [ObservableProperty]
    private string _gameTitle = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PlayLogEntry> _entries = new();

    public GamePlayLogViewModel(DatabaseService dbService, Game game)
    {
        _dbService = dbService;
        _gameTitle = game.Title;
        _ = LoadAsync(game.Id);
    }

    private async Task LoadAsync(int gameId)
    {
        var sessions = await _dbService.GetSessionsForGameAsync(gameId);
        Entries.Clear();
        foreach (var s in sessions)
        {
            Entries.Add(new PlayLogEntry
            {
                Date = s.StartTime.ToString("yyyy-MM-dd"),
                Time = $"{s.StartTime:HH:mm} ~ {s.EndTime:HH:mm}",
                Duration = FormatDuration(s.Duration)
            });
        }
    }

    private static string FormatDuration(System.TimeSpan ts)
    {
        int h = (int)ts.TotalHours;
        int m = ts.Minutes;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }
}

public class PlayLogEntry
{
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string Duration { get; set; } = "";
}
