using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls.Dialogs;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class RecommendationViewModel : ObservableObject
{
    private readonly DatabaseService _dbService;
    private readonly RecommendationService _recommendationService;
    private readonly AiRecommendationService _aiRecommendationService;
    private readonly IDialogCoordinator _dialogCoordinator;
    private readonly Action<Game>? _onGameImported;

    [ObservableProperty]
    private ObservableCollection<RecommendationCandidate> _recommendations = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _aiStatusText = string.Empty;

    [ObservableProperty]
    private string _profileSummaryText = string.Empty;

    [ObservableProperty]
    private string _topTagsText = string.Empty;

    [ObservableProperty]
    private string _avoidTagsText = string.Empty;

    [ObservableProperty]
    private string _preferredDevelopersText = string.Empty;

    [ObservableProperty]
    private string _profileDepthText = string.Empty;

    [ObservableProperty]
    private string _emptyStateText = string.Empty;

    [ObservableProperty]
    private bool _hasRecommendations;

    public RecommendationViewModel(
        DatabaseService dbService,
        SettingsService settingsService,
        System.Net.Http.HttpClient httpClient,
        IDialogCoordinator dialogCoordinator,
        Action<Game>? onGameImported = null)
    {
        _dbService = dbService;
        _recommendationService = new RecommendationService(dbService, httpClient, new VndbRecommendationCacheService());
        _aiRecommendationService = new AiRecommendationService(httpClient, settingsService);
        _dialogCoordinator = dialogCoordinator;
        _onGameImported = onGameImported;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        EmptyStateText = string.Empty;
        AiStatusText = string.Empty;
        Recommendations.Clear();
        HasRecommendations = false;

        try
        {
            var result = await _recommendationService.GetRecommendationsAsync();
            UpdateSummary(result.ProfileSummary);

            if (!result.ProfileSummary.HasEnoughSourceGames)
            {
                EmptyStateText = TranslationService.Instance["Rec_EmptyNeedMoreData"];
                AiStatusText = _aiRecommendationService.IsConfigured
                    ? TranslationService.Instance["Rec_AiStatusEnabled"]
                    : TranslationService.Instance["Rec_AiStatusDisabled"];
                return;
            }

            if (result.Candidates.Count == 0)
            {
                EmptyStateText = TranslationService.Instance["Rec_EmptyNoResults"];
                AiStatusText = _aiRecommendationService.IsConfigured
                    ? TranslationService.Instance["Rec_AiStatusEnabled"]
                    : TranslationService.Instance["Rec_AiStatusDisabled"];
                return;
            }

            foreach (var candidate in result.Candidates)
            {
                candidate.Reason = BuildFallbackReason(candidate);
                candidate.Caution = BuildFallbackCaution(candidate);
                Recommendations.Add(candidate);
            }

            HasRecommendations = Recommendations.Count > 0;
            AiStatusText = _aiRecommendationService.IsConfigured
                ? TranslationService.Instance["Rec_AiStatusEnabled"]
                : TranslationService.Instance["Rec_AiStatusDisabled"];

            if (!_aiRecommendationService.IsConfigured)
                return;

            var aiResult = await _aiRecommendationService.ExplainAsync(
                result.ProfileSummary,
                Recommendations.Take(12).ToList());

            if (!aiResult.HasExplanations)
            {
                AiStatusText = string.IsNullOrWhiteSpace(aiResult.ErrorMessage)
                    ? TranslationService.Instance["Rec_AiStatusFallback"]
                    : string.Format(TranslationService.Instance["Rec_AiStatusFallbackWithReason"], aiResult.ErrorMessage);
                return;
            }

            foreach (var explanation in aiResult.Explanations)
            {
                var candidate = Recommendations.FirstOrDefault(item =>
                    string.Equals(item.VndbId, explanation.CandidateId, StringComparison.OrdinalIgnoreCase));
                if (candidate == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(explanation.Reason))
                    candidate.Reason = explanation.Reason.Trim();
                if (!string.IsNullOrWhiteSpace(explanation.Caution))
                    candidate.Caution = explanation.Caution.Trim();
                if (explanation.MatchingTags.Count > 0)
                    candidate.MatchingTags = TagUtilities.Normalize(explanation.MatchingTags);
                if (!string.IsNullOrWhiteSpace(explanation.SellingPoint))
                    candidate.SellingPoint = explanation.SellingPoint.Trim();
            }
        }
        catch (Exception ex)
        {
            EmptyStateText = TranslationService.Instance["Rec_EmptyNoResults"];
            AiStatusText = TranslationService.Instance["Rec_AiStatusFallback"];
            Debug.WriteLine($"Recommendation refresh error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ImportRecommendationAsync(RecommendationCandidate? candidate)
    {
        if (candidate == null || !candidate.CanImport)
            return;

        try
        {
            var existingGames = await _dbService.GetAllGamesAsync();
            if (existingGames.Any(game =>
                    string.Equals(
                        VndbIdUtilities.Normalize(game.VndbId),
                        candidate.VndbId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                candidate.IsAlreadyInLibrary = true;
                return;
            }

            var game = new Game
            {
                Title = candidate.ImportTitle,
                Brand = candidate.Brand,
                ReleaseDate = candidate.ReleaseDate,
                Status = GameStatus.Planned,
                VndbId = candidate.VndbId,
                OfficialWebsite = candidate.OfficialWebsite,
                Tags = TagUtilities.Serialize(candidate.Tags),
                ProcessName = string.Empty,
                ExecutablePath = string.Empty
            };

            await _dbService.AddGameAsync(game);
            await _dbService.RecordRecommendationSignalAsync(candidate.VndbId, positiveDelta: 1.2);
            candidate.IsAlreadyInLibrary = true;
            candidate.FeedbackVote = 1;
            _onGameImported?.Invoke(game);
        }
        catch (Exception ex)
        {
            await _dialogCoordinator.ShowMessageAsync(
                this,
                TranslationService.Instance["Msg_ErrorTitle"],
                string.Format(TranslationService.Instance["Rec_ImportFailed"], ex.Message));
        }
    }

    [RelayCommand]
    private async Task LikeRecommendationAsync(RecommendationCandidate? candidate)
    {
        if (candidate == null)
            return;

        await _dbService.RecordRecommendationSignalAsync(candidate.VndbId, positiveDelta: 1.0);
        candidate.FeedbackVote = 1;
    }

    [RelayCommand]
    private async Task DislikeRecommendationAsync(RecommendationCandidate? candidate)
    {
        if (candidate == null)
            return;

        await _dbService.RecordRecommendationSignalAsync(candidate.VndbId, negativeDelta: 1.0);
        candidate.FeedbackVote = -1;
    }

    [RelayCommand]
    private async Task OpenVndbAsync(RecommendationCandidate? candidate)
    {
        if (candidate?.VndbUrl == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = candidate.VndbUrl,
                UseShellExecute = true
            });

            await _dbService.RecordRecommendationSignalAsync(candidate.VndbId, positiveDelta: 0.2);
        }
        catch
        {
            // Ignore launch failures.
        }
    }

    private void UpdateSummary(TasteProfileSummary profileSummary)
    {
        ProfileSummaryText = string.Format(
            TranslationService.Instance["Rec_ProfileSummary"],
            profileSummary.EligibleLibraryGames,
            profileSummary.TotalLibraryGames);

        TopTagsText = profileSummary.TopPositiveTags.Count == 0
            ? string.Format(TranslationService.Instance["Rec_TopTags"], TranslationService.Instance["Rec_None"])
            : string.Format(
                TranslationService.Instance["Rec_TopTags"],
                string.Join(", ", profileSummary.TopPositiveTags));

        AvoidTagsText = profileSummary.NegativeTags.Count == 0
            ? string.Format(TranslationService.Instance["Rec_AvoidTags"], TranslationService.Instance["Rec_None"])
            : string.Format(
                TranslationService.Instance["Rec_AvoidTags"],
                string.Join(", ", profileSummary.NegativeTags));

        PreferredDevelopersText = profileSummary.PreferredDevelopers.Count == 0
            ? string.Format(TranslationService.Instance["Rec_PreferredDevelopers"], TranslationService.Instance["Rec_None"])
            : string.Format(
                TranslationService.Instance["Rec_PreferredDevelopers"],
                string.Join(", ", profileSummary.PreferredDevelopers));

        ProfileDepthText = string.Format(
            CultureInfo.InvariantCulture,
            TranslationService.Instance["Rec_ProfileDepth"],
            profileSummary.CompletionRate.ToString("P0", CultureInfo.InvariantCulture),
            profileSummary.AverageCompletedHours.ToString("F1", CultureInfo.InvariantCulture),
            profileSummary.AverageDroppedHours.ToString("F1", CultureInfo.InvariantCulture),
            profileSummary.PreferenceStyle);
    }

    private static string BuildFallbackReason(RecommendationCandidate candidate)
    {
        var parts = new List<string>();

        if (candidate.MatchingTags.Count > 0)
        {
            parts.Add(string.Format(
                TranslationService.Instance["Rec_LocalReasonTags"],
                string.Join(", ", candidate.MatchingTags)));
        }

        if (candidate.MatchingDevelopers.Count > 0)
        {
            parts.Add(string.Format(
                TranslationService.Instance["Rec_LocalReasonDeveloper"],
                string.Join(", ", candidate.MatchingDevelopers)));
        }

        if (candidate.SourceMatches.Count > 0)
        {
            parts.Add(string.Format(
                TranslationService.Instance["Rec_LocalReasonSource"],
                string.Join(", ", candidate.SourceMatches.Select(match => match.Title))));
        }

        if (candidate.YearAffinity >= 0.6)
        {
            parts.Add(TranslationService.Instance["Rec_LocalReasonYear"]);
        }

        if (candidate.FeedbackAffinity > 0.15)
        {
            parts.Add(TranslationService.Instance["Rec_LocalReasonFeedback"]);
        }

        return parts.Count == 0
            ? TranslationService.Instance["Rec_LocalReasonFallback"]
            : string.Join(" ", parts);
    }

    private static string BuildFallbackCaution(RecommendationCandidate candidate)
    {
        var cautions = new List<string>();

        if (candidate.ConflictingTags.Count > 0)
        {
            cautions.Add(string.Format(
                TranslationService.Instance["Rec_LocalCautionTags"],
                string.Join(", ", candidate.ConflictingTags)));
        }

        if (candidate.YearAffinity > 0 && candidate.YearAffinity < 0.25)
            cautions.Add(TranslationService.Instance["Rec_LocalCautionYear"]);

        if (candidate.FeedbackAffinity < -0.2)
            cautions.Add(TranslationService.Instance["Rec_LocalCautionFeedback"]);

        return string.Join(" ", cautions);
    }
}
