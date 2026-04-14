using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Perelegans.Services;

namespace Perelegans.Models;

public partial class RecommendationCandidate : ObservableObject
{
    public string VndbId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public string? VndbUrl { get; set; }
    public string? OfficialWebsite { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> MatchingTags { get; set; } = [];
    public List<string> ConflictingTags { get; set; } = [];
    public List<string> MatchingDevelopers { get; set; } = [];
    public double RecommendationScore { get; set; }
    public double TagOverlapScore { get; set; }
    public double DeveloperBonus { get; set; }
    public double YearAffinity { get; set; }

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private string _caution = string.Empty;

    [ObservableProperty]
    private bool _isAlreadyInLibrary;

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(OriginalTitle) ? Title : OriginalTitle;

    public string ImportTitle => DisplayTitle;

    public bool CanImport => !IsAlreadyInLibrary;

    public string ImportButtonText => IsAlreadyInLibrary
        ? TranslationService.Instance["Rec_AlreadyInLibrary"]
        : TranslationService.Instance["Rec_Import"];

    partial void OnIsAlreadyInLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(ImportButtonText));
    }
}
