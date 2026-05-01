using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Perelegans.Services;

namespace Perelegans.Models;

public enum RecommendationMode
{
    Taste,
    Explore
}

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
    public List<RecommendationSourceMatch> SourceMatches { get; set; } = [];
    public double RecommendationScore { get; set; }
    public double TagOverlapScore { get; set; }
    public double DeveloperBonus { get; set; }
    public double YearAffinity { get; set; }
    public double FeedbackAffinity { get; set; }
    public double RecencyAlignment { get; set; }
    public string ScoreBreakdown { get; set; } = string.Empty;
    public string SourceMatchSummary { get; set; } = string.Empty;
    public int? VndbRank { get; set; }
    public double? VndbRating { get; set; }
    public int? VndbVoteCount { get; set; }
    public double? AiAffinityScore { get; set; }
    public double? BangumiRating { get; set; }
    public int? BangumiRank { get; set; }
    public int? BangumiVoteCount { get; set; }
    public double? ExternalRatingScore { get; set; }

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private string _caution = string.Empty;

    [ObservableProperty]
    private bool _isAlreadyInLibrary;

    [ObservableProperty]
    private string _sellingPoint = string.Empty;

    [ObservableProperty]
    private int _feedbackVote;

    [ObservableProperty]
    private string? _bangumiId;

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(OriginalTitle) ? Title : OriginalTitle;

    public string ImportTitle => DisplayTitle;

    public bool CanImport => !IsAlreadyInLibrary;
    public bool CanLike => FeedbackVote <= 0;
    public bool CanDislike => FeedbackVote >= 0;
    public bool HasBangumiUrl => !string.IsNullOrWhiteSpace(BangumiId);

    public string ImportButtonText => IsAlreadyInLibrary
        ? TranslationService.Instance["Rec_AlreadyInLibrary"]
        : TranslationService.Instance["Rec_Import"];

    public string LikeButtonText => FeedbackVote > 0
        ? TranslationService.Instance["Rec_FeedbackLiked"]
        : TranslationService.Instance["Rec_FeedbackLike"];

    public string DislikeButtonText => FeedbackVote < 0
        ? TranslationService.Instance["Rec_FeedbackDisliked"]
        : TranslationService.Instance["Rec_FeedbackDislike"];

    partial void OnIsAlreadyInLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(ImportButtonText));
    }

    partial void OnFeedbackVoteChanged(int value)
    {
        OnPropertyChanged(nameof(CanLike));
        OnPropertyChanged(nameof(CanDislike));
        OnPropertyChanged(nameof(LikeButtonText));
        OnPropertyChanged(nameof(DislikeButtonText));
    }

    partial void OnBangumiIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBangumiUrl));
    }
}
