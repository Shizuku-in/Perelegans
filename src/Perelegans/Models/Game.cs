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
    Completed = 2,
    Planned = 3
}

public class Game : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _brand = string.Empty;
    private DateTime? _releaseDate;
    private GameStatus _status = GameStatus.Playing;
    private string _processName = string.Empty;
    private string _executablePath = string.Empty;
    private TimeSpan _playtime = TimeSpan.Zero;
    private DateTime _createdDate = DateTime.Now;
    private DateTime _accessedDate = DateTime.Now;
    private string? _vndbId;
    private string? _erogameSpaceId;
    private string? _bangumiId;
    private string? _officialWebsite;
    private string? _tags;
    private bool _isDetectedRunning;
    private string? _coverImageUrl;
    private string? _coverImagePath;
    private double? _coverAspectRatio;

    public int Id { get; set; }
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Brand
    {
        get => _brand;
        set => SetField(ref _brand, value);
    }

    public DateTime? ReleaseDate
    {
        get => _releaseDate;
        set => SetField(ref _releaseDate, value);
    }

    public GameStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetField(ref _executablePath, value);
    }

    public TimeSpan Playtime
    {
        get => _playtime;
        set => SetField(ref _playtime, value);
    }

    public DateTime CreatedDate
    {
        get => _createdDate;
        set => SetField(ref _createdDate, value);
    }

    public DateTime AccessedDate
    {
        get => _accessedDate;
        set => SetField(ref _accessedDate, value);
    }

    // Metadata fields
    public string? VndbId
    {
        get => _vndbId;
        set => SetField(ref _vndbId, value);
    }

    public string? ErogameSpaceId
    {
        get => _erogameSpaceId;
        set => SetField(ref _erogameSpaceId, value);
    }

    public string? BangumiId
    {
        get => _bangumiId;
        set => SetField(ref _bangumiId, value);
    }

    public string? OfficialWebsite
    {
        get => _officialWebsite;
        set => SetField(ref _officialWebsite, value);
    }

    public string? Tags
    {
        get => _tags;
        set => SetField(ref _tags, value);
    }

    public string? CoverImageUrl
    {
        get => _coverImageUrl;
        set
        {
            if (_coverImageUrl == value) return;
            _coverImageUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverDisplaySource));
        }
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (_coverImagePath == value) return;
            _coverImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverDisplaySource));
        }
    }

    [NotMapped]
    public string? CoverDisplaySource => !string.IsNullOrWhiteSpace(CoverImagePath) ? CoverImagePath : CoverImageUrl;

    [NotMapped]
    public double? CoverAspectRatio
    {
        get => _coverAspectRatio;
        set
        {
            if (_coverAspectRatio == value) return;
            _coverAspectRatio = value;
            OnPropertyChanged();
        }
    }

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

    public void RefreshCoverBindings()
    {
        OnPropertyChanged(nameof(CoverImagePath));
        OnPropertyChanged(nameof(CoverImageUrl));
        OnPropertyChanged(nameof(CoverDisplaySource));
        OnPropertyChanged(nameof(CoverAspectRatio));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
