using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace Perelegans.Models;

public enum GameStatus
{
    Playing = 0,
    Dropped = 1,
    Completed = 2
}

public class Game : INotifyPropertyChanged
{
    private bool _isDetectedRunning;

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Playing;
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public TimeSpan Playtime { get; set; } = TimeSpan.Zero;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime AccessedDate { get; set; } = DateTime.Now;

    // Metadata fields
    public string? VndbId { get; set; }
    public string? ErogameSpaceId { get; set; }
    public string? BangumiId { get; set; }
    public string? OfficialWebsite { get; set; }

    // Navigation property
    public List<PlaySession> PlaySessions { get; set; } = new();

    [NotMapped]
    public bool IsDetectedRunning
    {
        get => _isDetectedRunning;
        set
        {
            if (_isDetectedRunning == value) return;
            _isDetectedRunning = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
