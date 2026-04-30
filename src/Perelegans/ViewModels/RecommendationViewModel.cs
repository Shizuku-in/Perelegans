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
    private List<RecommendationCandidate> _currentCandidates = [];

    public IReadOnlyList<RecommendationSortModeOption> SortModeOptions { get; } =
    [
        new(RecommendationSortMode.Smart, TranslationService.Instance["Rec_SortSmart"]),
        new(RecommendationSortMode.ComprehensiveRank, TranslationService.Instance["Rec_SortComprehensiveRank"])
    ];

    [ObservableProperty]
    private ObservableCollection<RecommendationCandidate> _recommendations = new();

    [ObservableProperty]
    private RecommendationSortMode _sortMode = RecommendationSortMode.Smart;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _aiStatusText = string.Empty;

    [ObservableProperty]
    private string _aiProfileText = string.Empty;

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

    public ObservableCollection<WorkflowStepViewModel> WorkflowSteps { get; } =
    [
        new(TranslationService.Instance["Rec_StepProfile"], TranslationService.Instance["Workflow_Waiting"]),
        new(TranslationService.Instance["Rec_StepBangumi"], TranslationService.Instance["Workflow_Waiting"]),
        new(TranslationService.Instance["Rec_StepAi"], TranslationService.Instance["Workflow_Waiting"])
    ];

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
        AiProfileText = string.Empty;
        ResetWorkflowSteps();
        SetWorkflowStep(0, "Workflow_Running");
        _currentCandidates = [];
        Recommendations.Clear();
        HasRecommendations = false;

        try
        {
            var result = await _recommendationService.GetRecommendationsAsync();
            UpdateSummary(result.ProfileSummary);
            SetWorkflowStep(0, "Workflow_Done");

            if (!result.ProfileSummary.HasEnoughSourceGames)
            {
                SetWorkflowStep(1, "Workflow_Skipped");
                SetWorkflowStep(2, "Workflow_Skipped");
                EmptyStateText = TranslationService.Instance["Rec_EmptyNeedMoreData"];
                AiStatusText = _aiRecommendationService.IsConfigured
                    ? TranslationService.Instance["Rec_AiStatusEnabled"]
                    : TranslationService.Instance["Rec_AiStatusDisabled"];
                return;
            }

            if (result.Candidates.Count == 0)
            {
                SetWorkflowStep(1, "Workflow_Skipped");
                SetWorkflowStep(2, "Workflow_Skipped");
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
            }

            _currentCandidates = result.Candidates.ToList();
            ApplySort();
            AiStatusText = _aiRecommendationService.IsConfigured
                ? TranslationService.Instance["Rec_AiStatusEnabled"]
                : TranslationService.Instance["Rec_AiStatusDisabled"];

            StartBangumiEnrichmentInBackground();
            SetWorkflowStep(1, "Workflow_Running");

            if (!_aiRecommendationService.IsConfigured)
            {
                SetWorkflowStep(1, "Workflow_Done");
                SetWorkflowStep(2, "Workflow_Skipped");
                return;
            }

            SetWorkflowStep(1, "Workflow_Done");

            SetWorkflowStep(2, "Workflow_Running");
            StartAiEnhancementInBackground(result.ProfileSummary);
        }
        catch (Exception ex)
        {
            EmptyStateText = TranslationService.Instance["Rec_EmptyNoResults"];
            AiStatusText = TranslationService.Instance["Rec_AiStatusFallback"];
            MarkFirstWaitingWorkflowStepAsFailed();
            Debug.WriteLine($"Recommendation refresh error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetWorkflowSteps()
    {
        foreach (var step in WorkflowSteps)
            step.StatusText = TranslationService.Instance["Workflow_Waiting"];
    }

    private void SetWorkflowStep(int index, string statusResourceKey)
    {
        if (index < 0 || index >= WorkflowSteps.Count)
            return;

        WorkflowSteps[index].StatusText = TranslationService.Instance[statusResourceKey];
    }

    private void StartBangumiEnrichmentInBackground()
    {
        var candidates = _currentCandidates.ToList();
        if (candidates.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _recommendationService.EnrichCandidatesWithBangumiRatingsAsync(candidates);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(ApplySort);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bangumi recommendation enrichment error: {ex.Message}");
            }
        });
    }

    private void StartAiEnhancementInBackground(TasteProfileSummary profileSummary)
    {
        var candidates = SelectCandidatesForAiRerank();
        var tagNames = _currentCandidates
            .SelectMany(candidate => candidate.Tags
                .Concat(candidate.MatchingTags)
                .Concat(candidate.ConflictingTags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                var tagWeightTask = RefreshAiTagWeightsAsync(tagNames);
                var aiResult = await _aiRecommendationService.ExplainAsync(profileSummary, candidates);
                await tagWeightTask;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!aiResult.HasExplanations)
                    {
                        AiProfileText = aiResult.UserProfileSummary;
                        AiStatusText = string.IsNullOrWhiteSpace(aiResult.ErrorMessage)
                            ? TranslationService.Instance["Rec_AiStatusFallback"]
                            : string.Format(TranslationService.Instance["Rec_AiStatusFallbackWithReason"], aiResult.ErrorMessage);
                        SetWorkflowStep(2, "Workflow_Fallback");
                        return;
                    }

                    foreach (var explanation in aiResult.Explanations)
                    {
                        var candidate = _currentCandidates.FirstOrDefault(item =>
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
                        if (explanation.AffinityScore.HasValue)
                            candidate.AiAffinityScore = explanation.AffinityScore.Value;
                    }

                    AiProfileText = aiResult.UserProfileSummary;
                    SetWorkflowStep(2, "Workflow_Done");
                    ApplySort();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AI recommendation enhancement error: {ex.Message}");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AiStatusText = TranslationService.Instance["Rec_AiStatusFallback"];
                    SetWorkflowStep(2, "Workflow_Fallback");
                });
            }
        });
    }

    private async Task RefreshAiTagWeightsAsync(IReadOnlyCollection<string> tagNames)
    {
        if (!_aiRecommendationService.IsConfigured || tagNames.Count == 0)
            return;

        var weights = await _aiRecommendationService.ClassifyTagWeightsAsync(tagNames);
        if (weights.Count == 0)
            return;

        var cacheService = new VndbRecommendationCacheService();
        var cache = await cacheService.LoadAsync();
        foreach (var (key, value) in weights)
            cache.TagWeights[key] = value;
        await cacheService.SaveAsync(cache);
    }

    private void MarkFirstWaitingWorkflowStepAsFailed()
    {
        var step = WorkflowSteps.FirstOrDefault(item =>
            item.StatusText == TranslationService.Instance["Workflow_Running"] ||
            item.StatusText == TranslationService.Instance["Workflow_Waiting"]);
        if (step != null)
            step.StatusText = TranslationService.Instance["Workflow_Failed"];
    }

    partial void OnSortModeChanged(RecommendationSortMode value)
    {
        ApplySort();
    }

    public void ApplySort()
    {
        IEnumerable<RecommendationCandidate> sorted = SortMode switch
        {
            RecommendationSortMode.ComprehensiveRank => _currentCandidates
                .OrderByDescending(ComputeComprehensiveRankScore)
                .ThenBy(candidate => candidate.BangumiRank ?? int.MaxValue)
                .ThenBy(candidate => candidate.VndbRank ?? int.MaxValue)
                .ThenByDescending(candidate => (candidate.BangumiVoteCount ?? 0) + (candidate.VndbVoteCount ?? 0))
                .ThenByDescending(candidate => candidate.RecommendationScore)
                .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            _ => _currentCandidates
                .OrderByDescending(ComputeSmartScore)
                .ThenByDescending(ComputeComprehensiveRankScore)
                .ThenByDescending(candidate => candidate.TagOverlapScore)
                .ThenByDescending(candidate => candidate.FeedbackAffinity)
                .ThenByDescending(candidate => candidate.DeveloperBonus)
                .ThenByDescending(candidate => candidate.YearAffinity)
                .ThenByDescending(candidate => candidate.ReleaseDate ?? DateTime.MinValue)
                .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase)
        };

        Recommendations.Clear();
        foreach (var candidate in sorted)
            Recommendations.Add(candidate);

        HasRecommendations = Recommendations.Count > 0;
    }

    private static double ComputeComprehensiveRankScore(RecommendationCandidate candidate)
    {
        return candidate.ExternalRatingScore
               ?? NormalizeRating(candidate.BangumiRating)
               ?? NormalizeRating(candidate.VndbRating)
               ?? 0.0;
    }

    private static double ComputeSmartScore(RecommendationCandidate candidate)
    {
        var externalScore = ComputeComprehensiveRankScore(candidate);

        if (!candidate.AiAffinityScore.HasValue)
        {
            return candidate.RecommendationScore * 0.70
                   + externalScore * 0.30;
        }

        return candidate.RecommendationScore * 0.48
               + candidate.AiAffinityScore.Value * 0.22
               + externalScore * 0.30;
    }

    private List<RecommendationCandidate> SelectCandidatesForAiRerank()
    {
        var selected = new Dictionary<string, RecommendationCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in _currentCandidates
                     .OrderByDescending(candidate => candidate.RecommendationScore)
                     .ThenByDescending(candidate => candidate.TagOverlapScore)
                     .Take(6))
        {
            selected[candidate.VndbId] = candidate;
        }

        foreach (var candidate in _currentCandidates
                     .OrderByDescending(ComputeComprehensiveRankScore)
                     .ThenBy(candidate => candidate.BangumiRank ?? int.MaxValue)
                     .ThenBy(candidate => candidate.VndbRank ?? int.MaxValue)
                     .Take(2))
        {
            selected.TryAdd(candidate.VndbId, candidate);
        }

        return selected.Values.ToList();
    }

    private static double? NormalizeRating(double? rating)
    {
        if (!rating.HasValue || rating.Value <= 0)
            return null;

        var value = rating.Value > 10 ? rating.Value / 100d : rating.Value / 10d;
        return Math.Clamp(value, 0, 1);
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
                BangumiId = candidate.BangumiId,
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
        _currentCandidates.RemoveAll(item =>
            string.Equals(item.VndbId, candidate.VndbId, StringComparison.OrdinalIgnoreCase));
        ApplySort();
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

    [RelayCommand]
    private async Task OpenBangumiAsync(RecommendationCandidate? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate?.BangumiId))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://bgm.tv/subject/{candidate.BangumiId.Trim()}",
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

public sealed class RecommendationSortModeOption(RecommendationSortMode value, string label)
{
    public RecommendationSortMode Value { get; } = value;
    public string Label { get; } = label;
}
