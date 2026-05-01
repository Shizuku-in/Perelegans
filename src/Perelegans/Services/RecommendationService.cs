using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class RecommendationService
{
    private const string VndbApiUrl = "https://api.vndb.org/kana/vn";
    private const string DetailFields = "id, title, alttitle, released, rank, rating, votecount, developers.name, tags.id, tags.name, tags.rating, extlinks.url, extlinks.label, extlinks.name";
    private const string DetailFieldsWithoutRank = "id, title, alttitle, released, rating, votecount, developers.name, tags.id, tags.name, tags.rating, extlinks.url, extlinks.label, extlinks.name";
    private const int CacheTtlDays = 7;
    private const int CandidateSearchPageSize = 100;
    private const int CandidateEvaluationLimit = 48;
    private const int ExploreCandidateEvaluationLimit = 72;
    private const int FinalRecommendationLimit = 24;
    private const int MinimumFallbackResultCount = 12;
    private const string ProfileCacheVersion = "profile-v2";
    private static readonly TimeSpan BangumiSearchCacheTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan CandidateSearchCacheTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan BangumiRequestTimeout = TimeSpan.FromSeconds(5);
    private static int _libraryCrossSourceEnrichmentRunning;
    private static readonly RecommendationScoringOptions Scoring = new();
    private readonly DatabaseService _dbService;
    private readonly HttpClient _httpClient;
    private readonly VndbRecommendationCacheService _cacheService;

    public RecommendationService(DatabaseService dbService, HttpClient httpClient, VndbRecommendationCacheService cacheService)
    {
        _dbService = dbService;
        _httpClient = httpClient;
        _cacheService = cacheService;
    }

    public async Task<RecommendationResult> GetRecommendationsAsync(RecommendationMode mode = RecommendationMode.Taste)
    {
        var context = await GetProfileContextAsync(useCache: true);
        var result = new RecommendationResult
        {
            ProfileSummary = context.ProfileSummary
        };

        if (context.Profile == null)
            return result;

        var candidatePool = mode == RecommendationMode.Explore
            ? await SearchExploreCandidatesAsync(
                context.Profile,
                context.LibraryIds,
                context.FeedbackById,
                context.CacheDocument)
            : await SearchCandidatesAsync(
                context.Profile,
                context.LibraryIds,
                context.FeedbackById,
                context.CacheDocument);

        var enrichedCandidates = candidatePool
            .OrderByDescending(candidate => mode == RecommendationMode.Explore
                ? ComputePreEnrichmentExploreScore(candidate)
                : candidate.RecommendationScore)
            .ThenByDescending(candidate => candidate.RecommendationScore)
            .ThenByDescending(candidate => candidate.ExternalRatingScore ?? NormalizeRating(candidate.VndbRating) ?? 0)
            .ThenBy(candidate => candidate.VndbRank ?? int.MaxValue)
            .ThenByDescending(candidate => candidate.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .Take(mode == RecommendationMode.Explore ? ExploreCandidateEvaluationLimit : CandidateEvaluationLimit)
            .ToList();

        await EnrichCandidatesWithBangumiRatingsAsync(
            enrichedCandidates.Take(Scoring.BlockingBangumiEnrichmentLimit).ToList());

        var rankedCandidates = RankCandidates(enrichedCandidates, mode).ToList();

        result.Candidates = DiversifyCandidates(
                rankedCandidates.Where(IsRelevantCandidate),
                FinalRecommendationLimit)
            .ToList();

        if (result.Candidates.Count == 0)
        {
            result.Candidates = DiversifyCandidates(
                    rankedCandidates
                        .Where(candidate => candidate.RecommendationScore > 0)
                        .Take(MinimumFallbackResultCount * 2),
                    MinimumFallbackResultCount)
                .ToList();
        }

        return result;
    }

    private static IEnumerable<RecommendationCandidate> DiversifyCandidates(
        IEnumerable<RecommendationCandidate> rankedCandidates,
        int limit)
    {
        var remaining = rankedCandidates.ToList();
        var selected = new List<RecommendationCandidate>(limit);
        var developerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0 && selected.Count < limit)
        {
            var pickedIndex = remaining.FindIndex(candidate =>
                IsWithinDiversityBudget(candidate, developerCounts, tagCounts, selected.Count));

            if (pickedIndex < 0)
                pickedIndex = 0;

            var picked = remaining[pickedIndex];
            remaining.RemoveAt(pickedIndex);
            selected.Add(picked);

            foreach (var developer in picked.MatchingDevelopers.DefaultIfEmpty(picked.Brand).Where(value => !string.IsNullOrWhiteSpace(value)))
                developerCounts[developer] = developerCounts.TryGetValue(developer, out var count) ? count + 1 : 1;

            foreach (var tag in picked.MatchingTags.Take(2))
                tagCounts[tag] = tagCounts.TryGetValue(tag, out var count) ? count + 1 : 1;
        }

        return selected;
    }

    private static bool IsWithinDiversityBudget(
        RecommendationCandidate candidate,
        IReadOnlyDictionary<string, int> developerCounts,
        IReadOnlyDictionary<string, int> tagCounts,
        int selectedCount)
    {
        var developerLimit = selectedCount < 12 ? 2 : 3;
        var tagLimit = selectedCount < 12 ? 4 : 6;

        var developers = candidate.MatchingDevelopers.Count > 0
            ? candidate.MatchingDevelopers
            : string.IsNullOrWhiteSpace(candidate.Brand) ? [] : [candidate.Brand];

        if (developers.Any(developer => developerCounts.TryGetValue(developer, out var count) && count >= developerLimit))
            return false;

        if (candidate.MatchingTags.Take(2).Any(tag => tagCounts.TryGetValue(tag, out var count) && count >= tagLimit))
            return false;

        return true;
    }

    private static double ComputeCandidateSelectionScore(RecommendationCandidate candidate)
    {
        var externalScore = candidate.ExternalRatingScore ?? NormalizeRating(candidate.VndbRating) ?? 0;
        return candidate.RecommendationScore * Scoring.ProfileSelectionWeight
               + externalScore * Scoring.ExternalSelectionWeight;
    }

    private static IEnumerable<RecommendationCandidate> RankCandidates(
        IEnumerable<RecommendationCandidate> candidates,
        RecommendationMode mode)
    {
        if (mode == RecommendationMode.Explore)
        {
            return candidates
                .OrderByDescending(ComputeExploreSelectionScore)
                .ThenByDescending(ComputeLeaderboardScore)
                .ThenBy(candidate => candidate.BangumiRank ?? int.MaxValue)
                .ThenBy(candidate => candidate.VndbRank ?? int.MaxValue)
                .ThenByDescending(candidate => (candidate.BangumiVoteCount ?? 0) + (candidate.VndbVoteCount ?? 0))
                .ThenByDescending(candidate => candidate.RecommendationScore)
                .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase);
        }

        return candidates
            .OrderByDescending(ComputeCandidateSelectionScore)
            .ThenByDescending(candidate => candidate.RecommendationScore)
            .ThenByDescending(candidate => candidate.ExternalRatingScore ?? NormalizeRating(candidate.VndbRating) ?? 0)
            .ThenBy(candidate => candidate.BangumiRank ?? int.MaxValue)
            .ThenBy(candidate => candidate.VndbRank ?? int.MaxValue)
            .ThenByDescending(candidate => candidate.TagOverlapScore)
            .ThenByDescending(candidate => candidate.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase);
    }

    private static double ComputeExploreSelectionScore(RecommendationCandidate candidate)
    {
        return ComputeLeaderboardScore(candidate) * 0.75
               + Math.Clamp(candidate.RecommendationScore, 0, 1) * 0.25;
    }

    private static double ComputePreEnrichmentExploreScore(RecommendationCandidate candidate)
    {
        return ComputeVndbLeaderboardScore(candidate) * 0.75
               + Math.Clamp(candidate.RecommendationScore, 0, 1) * 0.25;
    }

    private static double ComputeLeaderboardScore(RecommendationCandidate candidate)
    {
        var bangumiScore = ComputeBangumiLeaderboardScore(candidate);
        var vndbScore = ComputeVndbLeaderboardScore(candidate);
        if (bangumiScore > 0 && vndbScore > 0)
            return bangumiScore * 0.5 + vndbScore * 0.5;
        return Math.Max(bangumiScore, vndbScore);
    }

    private static double ComputeBangumiLeaderboardScore(RecommendationCandidate candidate)
    {
        var rankScore = ComputeRankScore(candidate.BangumiRank, 3000);
        var ratingScore = NormalizeRating(candidate.BangumiRating) ?? 0;
        return Math.Max(rankScore, ratingScore);
    }

    private static double ComputeVndbLeaderboardScore(RecommendationCandidate candidate)
    {
        var rankScore = ComputeRankScore(candidate.VndbRank, 5000);
        var ratingScore = NormalizeRating(candidate.VndbRating) ?? 0;
        return Math.Max(rankScore, ratingScore);
    }

    private static double ComputeRankScore(int? rank, int maxRank)
    {
        if (!rank.HasValue || rank.Value <= 0)
            return 0;

        return Math.Clamp(1d - (rank.Value - 1d) / maxRank, 0, 1);
    }

    public async Task WarmProfileCacheAsync()
    {
        await GetProfileContextAsync(useCache: false);
    }

    private void StartLibraryCrossSourceEnrichment(IReadOnlyCollection<Game> games)
    {
        if (games.Count == 0 || Interlocked.CompareExchange(ref _libraryCrossSourceEnrichmentRunning, 1, 0) != 0)
            return;

        var snapshot = games
            .Where(game =>
                (!string.IsNullOrWhiteSpace(game.VndbId) && string.IsNullOrWhiteSpace(game.BangumiId)) ||
                (string.IsNullOrWhiteSpace(game.VndbId) && !string.IsNullOrWhiteSpace(game.BangumiId)))
            .Select(CloneGameForBackgroundEnrichment)
            .ToList();

        if (snapshot.Count == 0)
        {
            Interlocked.Exchange(ref _libraryCrossSourceEnrichmentRunning, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var cacheDocument = await _cacheService.LoadAsync();
                await EnrichLibraryCrossSourceMetadataAsync(snapshot, cacheDocument);
            }
            catch
            {
                // Cross-source enrichment is opportunistic and must not block recommendations.
            }
            finally
            {
                Interlocked.Exchange(ref _libraryCrossSourceEnrichmentRunning, 0);
            }
        });
    }

    private static Game CloneGameForBackgroundEnrichment(Game game)
    {
        return new Game
        {
            Id = game.Id,
            Title = game.Title,
            Brand = game.Brand,
            ReleaseDate = game.ReleaseDate,
            Status = game.Status,
            ProcessName = game.ProcessName,
            ExecutablePath = game.ExecutablePath,
            Playtime = game.Playtime,
            CreatedDate = game.CreatedDate,
            AccessedDate = game.AccessedDate,
            VndbId = game.VndbId,
            ErogameSpaceId = game.ErogameSpaceId,
            BangumiId = game.BangumiId,
            OfficialWebsite = game.OfficialWebsite,
            Tags = game.Tags,
            CoverImageUrl = game.CoverImageUrl,
            CoverImagePath = game.CoverImagePath,
            CoverAspectRatio = game.CoverAspectRatio
        };
    }

    private async Task EnrichLibraryCrossSourceMetadataAsync(
        IReadOnlyCollection<Game> games,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var vndbOnlyGames = games
            .Where(game => !string.IsNullOrWhiteSpace(game.VndbId) && string.IsNullOrWhiteSpace(game.BangumiId))
            .ToList();
        var bangumiOnlyGames = games
            .Where(game => string.IsNullOrWhiteSpace(game.VndbId) && !string.IsNullOrWhiteSpace(game.BangumiId))
            .ToList();

        if (vndbOnlyGames.Count == 0 && bangumiOnlyGames.Count == 0)
            return;

        var bangumiService = new BangumiService(_httpClient);
        var bangumiQueryCache = new ConcurrentDictionary<string, Lazy<Task<List<MetadataResult>>>>(StringComparer.OrdinalIgnoreCase);
        var bangumiCacheLock = new object();

        if (vndbOnlyGames.Count > 0)
        {
            var ids = vndbOnlyGames
                .Select(game => VndbIdUtilities.Normalize(game.VndbId))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var vndbMetadataById = await GetVisualNovelsByIdsAsync(ids, cacheDocument);

            foreach (var game in vndbOnlyGames)
            {
                var vndbId = VndbIdUtilities.Normalize(game.VndbId);
                if (string.IsNullOrWhiteSpace(vndbId) || !vndbMetadataById.TryGetValue(vndbId, out var metadata))
                    continue;

                var candidate = BuildCandidateForCrossSourceMatch(metadata);
                var match = await FindBestBangumiMatchAsync(
                    bangumiService,
                    candidate,
                    cacheDocument,
                    bangumiCacheLock,
                    bangumiQueryCache);
                if (string.IsNullOrWhiteSpace(match?.SourceId))
                    continue;

                game.BangumiId = match.SourceId;
                MergeGameMetadataFromResult(game, match);
                await _dbService.UpdateGameAsync(game);
            }
        }

        if (bangumiOnlyGames.Count > 0)
        {
            foreach (var game in bangumiOnlyGames)
            {
                var bangumiMetadata = await WithTimeout(
                    bangumiService.GetByIdAsync(game.BangumiId!),
                    BangumiRequestTimeout);
                if (bangumiMetadata == null)
                    continue;

                var match = await FindBestVndbMatchAsync(game, bangumiMetadata, cacheDocument);
                if (match == null)
                    continue;

                game.VndbId = match.Id;
                MergeGameMetadataFromVndb(game, match);
                await _dbService.UpdateGameAsync(game);
            }
        }

        await _cacheService.SaveAsync(cacheDocument);
    }

    private async Task<RecommendationProfileContext> GetProfileContextAsync(bool useCache)
    {
        var games = await _dbService.GetAllGamesAsync();
        var profileSummary = new TasteProfileSummary
        {
            TotalLibraryGames = games.Count,
            CompletedGames = games.Count(game => game.Status == GameStatus.Completed),
            DroppedGames = games.Count(game => game.Status == GameStatus.Dropped)
        };

        profileSummary.CompletionRate = games.Count == 0
            ? 0
            : (double)profileSummary.CompletedGames / games.Count;
        profileSummary.AveragePlaytimeHours = games.Count == 0
            ? 0
            : games.Average(game => game.Playtime.TotalHours);
        profileSummary.AverageCompletedHours = games
            .Where(game => game.Status == GameStatus.Completed)
            .Select(game => game.Playtime.TotalHours)
            .DefaultIfEmpty(0)
            .Average();
        profileSummary.AverageDroppedHours = games
            .Where(game => game.Status == GameStatus.Dropped)
            .Select(game => game.Playtime.TotalHours)
            .DefaultIfEmpty(0)
            .Average();

        var cacheDocument = await _cacheService.LoadAsync();
        StartLibraryCrossSourceEnrichment(games);

        var libraryIds = games
            .Select(game => VndbIdUtilities.Normalize(game.VndbId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var feedbackById = await _dbService.GetRecommendationFeedbackMapAsync();
        var signature = BuildProfileSignature(games, feedbackById, cacheDocument.TagWeights, cacheDocument.TagAliases);

        if (useCache &&
            cacheDocument.ProfileCache is { Profile: not null } profileCache &&
            string.Equals(profileCache.Signature, signature, StringComparison.Ordinal))
        {
            return new RecommendationProfileContext(
                profileCache.Summary,
                FromCachedProfile(profileCache.Profile),
                libraryIds,
                feedbackById,
                cacheDocument);
        }

        var metadataById = await GetVisualNovelsByIdsAsync(libraryIds, cacheDocument);
        var feedbackMetadataById = await GetFeedbackMetadataByIdAsync(feedbackById, libraryIds, cacheDocument);
        var bangumiMetadataByGameId = await GetBangumiMetadataByGameIdAsync(games);

        var profiledGames = games
            .Select(game =>
            {
                var vndbId = VndbIdUtilities.Normalize(game.VndbId);
                var vndbMetadata = vndbId != null && metadataById.TryGetValue(vndbId, out var foundVndb)
                    ? foundVndb
                    : null;
                bangumiMetadataByGameId.TryGetValue(game.Id, out var bangumiMetadata);
                return (Game: game, Metadata: MergeLibraryMetadata(vndbMetadata, bangumiMetadata));
            })
            .Where(item => item.Metadata != null)
            .Select(item => (item.Game, Metadata: item.Metadata!))
            .ToList();

        profileSummary.EligibleLibraryGames = profiledGames.Count;
        if (profiledGames.Count < 3)
        {
            cacheDocument.ProfileCache = new CachedRecommendationProfile
            {
                Signature = signature,
                CachedAtUtc = DateTimeOffset.UtcNow,
                Summary = profileSummary,
                Profile = null
            };
            await _cacheService.SaveAsync(cacheDocument);
            return new RecommendationProfileContext(profileSummary, null, libraryIds, feedbackById, cacheDocument);
        }

        var profile = BuildProfile(
            profiledGames,
            feedbackMetadataById,
            feedbackById,
            cacheDocument.TagWeights,
            cacheDocument.TagAliases,
            profileSummary);
        if (profile.PositiveTagIds.Count == 0)
        {
            cacheDocument.ProfileCache = new CachedRecommendationProfile
            {
                Signature = signature,
                CachedAtUtc = DateTimeOffset.UtcNow,
                Summary = profileSummary,
                Profile = null
            };
            await _cacheService.SaveAsync(cacheDocument);
            return new RecommendationProfileContext(profileSummary, null, libraryIds, feedbackById, cacheDocument);
        }

        cacheDocument.ProfileCache = new CachedRecommendationProfile
        {
            Signature = signature,
            CachedAtUtc = DateTimeOffset.UtcNow,
            Summary = profileSummary,
            Profile = ToCachedProfile(profile)
        };
        await _cacheService.SaveAsync(cacheDocument);

        return new RecommendationProfileContext(profileSummary, profile, libraryIds, feedbackById, cacheDocument);
    }

    private async Task<Dictionary<string, CachedVndbVisualNovel>> GetVisualNovelsByIdsAsync(
        IReadOnlyCollection<string> ids,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var results = new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return results;

        var missingIds = new List<string>();
        foreach (var id in ids)
        {
            if (cacheDocument.Entries.TryGetValue(id, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAtUtc <= TimeSpan.FromDays(CacheTtlDays))
            {
                results[id] = cached;
            }
            else
            {
                missingIds.Add(id);
            }
        }

        if (missingIds.Count > 0)
        {
            var fetched = await FetchVisualNovelsByIdsAsync(missingIds);
            foreach (var visualNovel in fetched)
            {
                cacheDocument.Entries[visualNovel.Id] = visualNovel;
                results[visualNovel.Id] = visualNovel;
            }

            await _cacheService.SaveAsync(cacheDocument);

            foreach (var id in missingIds)
            {
                if (results.ContainsKey(id))
                    continue;
                if (cacheDocument.Entries.TryGetValue(id, out var stale))
                    results[id] = stale;
            }
        }

        return results;
    }

    private async Task<Dictionary<string, CachedVndbVisualNovel>> GetFeedbackMetadataByIdAsync(
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        HashSet<string> libraryIds,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var feedbackIds = feedbackById
            .Where(item => !libraryIds.Contains(item.Key))
            .Where(item => item.Value.PositiveSignal > 0 || item.Value.NegativeSignal > 0)
            .OrderByDescending(item => item.Value.UpdatedAt)
            .Take(80)
            .Select(item => item.Key)
            .ToList();

        return await GetVisualNovelsByIdsAsync(feedbackIds, cacheDocument);
    }

    private async Task<List<CachedVndbVisualNovel>> FetchVisualNovelsByIdsAsync(IReadOnlyList<string> ids)
    {
        var results = new List<CachedVndbVisualNovel>();
        for (var offset = 0; offset < ids.Count; offset += 20)
        {
            var batch = ids.Skip(offset).Take(20).ToList();
            if (batch.Count == 0)
                continue;

            var filters = BuildCompoundFilter(
                "or",
                batch.Select(id => (object)new object[] { "id", "=", id }));

            var responseJson = await PostToVndbAsync(filters, batch.Count, DetailFields, "id");
            if (responseJson != null)
                results.AddRange(ParseVisualNovels(responseJson));
        }

        return results;
    }

    private async Task<Dictionary<int, CachedVndbVisualNovel>> GetBangumiMetadataByGameIdAsync(IReadOnlyCollection<Game> games)
    {
        var gamesWithBangumi = games
            .Where(game => !string.IsNullOrWhiteSpace(game.BangumiId))
            .ToList();

        if (gamesWithBangumi.Count == 0)
            return [];

        var bangumiService = new BangumiService(_httpClient);
        var results = new ConcurrentDictionary<int, CachedVndbVisualNovel>();
        using var throttler = new SemaphoreSlim(4);

        var tasks = gamesWithBangumi.Select(async game =>
        {
            await throttler.WaitAsync();
            try
            {
                var metadata = await bangumiService.GetByIdAsync(game.BangumiId!);
                if (metadata == null)
                    return;

                results[game.Id] = ToBangumiProfileMetadata(metadata, game);
            }
            catch
            {
                // Bangumi metadata is an optional profile enrichment source.
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToDictionary(item => item.Key, item => item.Value);
    }

    private static CachedVndbVisualNovel ToBangumiProfileMetadata(MetadataResult metadata, Game game)
    {
        var title = !string.IsNullOrWhiteSpace(metadata.Title) ? metadata.Title : game.Title;
        var originalTitle = !string.IsNullOrWhiteSpace(metadata.OriginalTitle) ? metadata.OriginalTitle : title;

        var developers = TagUtilities.Normalize(
            (metadata.Brand ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var tags = TagUtilities.Normalize(metadata.Tags)
            .Select(tag => new CachedVndbTag
            {
                Id = BuildBangumiTagKey(tag),
                Name = tag,
                Rating = 1.0
            })
            .ToList();

        return new CachedVndbVisualNovel
        {
            Id = $"bgm:{metadata.SourceId}",
            Title = title,
            OriginalTitle = originalTitle,
            ReleaseDate = metadata.ReleaseDate ?? game.ReleaseDate,
            Rank = metadata.Rank,
            Rating = metadata.Rating,
            VoteCount = metadata.VoteCount,
            Developers = developers,
            Tags = tags,
            CachedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static CachedVndbVisualNovel? MergeLibraryMetadata(
        CachedVndbVisualNovel? vndbMetadata,
        CachedVndbVisualNovel? bangumiMetadata)
    {
        if (vndbMetadata == null)
            return bangumiMetadata;
        if (bangumiMetadata == null)
            return vndbMetadata;

        return new CachedVndbVisualNovel
        {
            Id = vndbMetadata.Id,
            Title = string.IsNullOrWhiteSpace(vndbMetadata.Title) ? bangumiMetadata.Title : vndbMetadata.Title,
            OriginalTitle = string.IsNullOrWhiteSpace(vndbMetadata.OriginalTitle) ? bangumiMetadata.OriginalTitle : vndbMetadata.OriginalTitle,
            ReleaseDate = vndbMetadata.ReleaseDate ?? bangumiMetadata.ReleaseDate,
            Rank = vndbMetadata.Rank,
            Rating = vndbMetadata.Rating ?? bangumiMetadata.Rating,
            VoteCount = vndbMetadata.VoteCount ?? bangumiMetadata.VoteCount,
            Developers = TagUtilities.Normalize(vndbMetadata.Developers.Concat(bangumiMetadata.Developers)),
            Tags = MergeTags(vndbMetadata.Tags, bangumiMetadata.Tags),
            ExternalLinks = vndbMetadata.ExternalLinks,
            CachedAtUtc = vndbMetadata.CachedAtUtc
        };
    }

    private static List<CachedVndbTag> MergeTags(
        IEnumerable<CachedVndbTag> primaryTags,
        IEnumerable<CachedVndbTag> secondaryTags)
    {
        return primaryTags
            .Concat(secondaryTags)
            .GroupBy(tag => tag.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(tag => tag.Rating).First())
            .ToList();
    }

    private static string BuildProfileSignature(
        IReadOnlyCollection<Game> games,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        IReadOnlyDictionary<string, CachedTagWeight> tagWeights,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ProfileCacheVersion);
        foreach (var game in games.OrderBy(game => game.Id))
        {
            builder
                .Append(game.Id).Append('|')
                .Append(game.Status).Append('|')
                .Append(game.Playtime.Ticks).Append('|')
                .Append(game.AccessedDate.Ticks).Append('|')
                .Append(VndbIdUtilities.Normalize(game.VndbId) ?? string.Empty).Append('|')
                .Append(game.BangumiId?.Trim() ?? string.Empty).Append('|')
                .Append(game.Tags?.Trim() ?? string.Empty)
                .AppendLine();
        }

        foreach (var feedback in feedbackById.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(feedback.Key).Append('|')
                .Append(feedback.Value.PositiveSignal.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(feedback.Value.NegativeSignal.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(feedback.Value.UpdatedAt.Ticks)
                .AppendLine();
        }

        foreach (var tagWeight in tagWeights.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(tagWeight.Key).Append('|')
                .Append(tagWeight.Value.Category).Append('|')
                .Append(tagWeight.Value.Weight.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(tagWeight.Value.Confidence.ToString("R", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        foreach (var tagAlias in tagAliases.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(tagAlias.Key).Append('|')
                .Append(tagAlias.Value.CanonicalTag).Append('|')
                .Append(tagAlias.Value.Category).Append('|')
                .Append(tagAlias.Value.Weight.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(tagAlias.Value.Confidence.ToString("R", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static CachedRecommendationProfileData ToCachedProfile(RecommendationProfile profile)
    {
        return new CachedRecommendationProfileData
        {
            TagScores = new Dictionary<string, double>(profile.TagScores, StringComparer.OrdinalIgnoreCase),
            RecentTagScores = new Dictionary<string, double>(profile.RecentTagScores, StringComparer.OrdinalIgnoreCase),
            PositiveTagIds = profile.PositiveTagIds.ToList(),
            SecondaryPositiveTagIds = profile.SecondaryPositiveTagIds.ToList(),
            NegativeTagIds = profile.NegativeTagIds.ToList(),
            SoftNegativeTagIds = profile.SoftNegativeTagIds.ToList(),
            DeveloperScores = new Dictionary<string, double>(profile.DeveloperScores, StringComparer.OrdinalIgnoreCase),
            PreferredDevelopers = profile.PreferredDevelopers.ToList(),
            PreferredReleaseYear = profile.PreferredReleaseYear,
            MaxPositiveOverlap = profile.MaxPositiveOverlap,
            MaxRecentOverlap = profile.MaxRecentOverlap,
            SourceGames = profile.SourceGames
                .Select(source => new CachedSourceGameProfile
                {
                    Title = source.Title,
                    Developers = source.Developers.ToList(),
                    ReleaseDate = source.ReleaseDate,
                    PositiveTagScores = new Dictionary<string, double>(source.PositiveTagScores, StringComparer.OrdinalIgnoreCase),
                    Weight = source.Weight,
                    RecentBias = source.RecentBias
                })
                .ToList(),
            TagAliases = profile.TagAliases.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            DeveloperPreferenceWeight = profile.DeveloperPreferenceWeight
        };
    }

    private static RecommendationProfile FromCachedProfile(CachedRecommendationProfileData profile)
    {
        profile.EnsureInitialized();
        return new RecommendationProfile
        {
            TagScores = new Dictionary<string, double>(profile.TagScores, StringComparer.OrdinalIgnoreCase),
            RecentTagScores = new Dictionary<string, double>(profile.RecentTagScores, StringComparer.OrdinalIgnoreCase),
            PositiveTagIds = profile.PositiveTagIds.ToList(),
            SecondaryPositiveTagIds = profile.SecondaryPositiveTagIds.ToList(),
            NegativeTagIds = profile.NegativeTagIds.ToList(),
            SoftNegativeTagIds = profile.SoftNegativeTagIds.ToList(),
            DeveloperScores = new Dictionary<string, double>(profile.DeveloperScores, StringComparer.OrdinalIgnoreCase),
            PreferredDevelopers = profile.PreferredDevelopers.ToList(),
            PreferredReleaseYear = profile.PreferredReleaseYear,
            MaxPositiveOverlap = profile.MaxPositiveOverlap,
            MaxRecentOverlap = profile.MaxRecentOverlap,
            SourceGames = profile.SourceGames
                .Select(source => new SourceGameProfile
                {
                    Title = source.Title,
                    Developers = source.Developers.ToList(),
                    ReleaseDate = source.ReleaseDate,
                    PositiveTagScores = new Dictionary<string, double>(source.PositiveTagScores, StringComparer.OrdinalIgnoreCase),
                    Weight = source.Weight,
                    RecentBias = source.RecentBias
                })
                .ToList(),
            TagAliases = profile.TagAliases.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            DeveloperPreferenceWeight = profile.DeveloperPreferenceWeight
        };
    }

    private async Task<List<RecommendationCandidate>> SearchCandidatesAsync(
        RecommendationProfile profile,
        HashSet<string> existingIds,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var recallPlans = new[]
        {
            new RecallPlan(profile.PositiveTagIds.Take(6).ToList(), profile.NegativeTagIds.Take(2).ToList(), 4, "rating"),
            new RecallPlan(
                profile.SecondaryPositiveTagIds.Count > 0 ? profile.SecondaryPositiveTagIds.Take(8).ToList() : profile.PositiveTagIds.Take(8).ToList(),
                [],
                3,
                "rating"),
            new RecallPlan(profile.PositiveTagIds.Take(3).ToList(), [], 2, "released"),
            new RecallPlan(profile.PositiveTagIds.Take(3).ToList(), [], 2, "rating", requireAllPositiveTags: true),
        };

        var visualNovelsById = new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);

        foreach (var recallPlan in recallPlans.Where(plan => plan.PositiveTagIds.Count > 0))
        {
            var positiveOperation = recallPlan.RequireAllPositiveTags ? "and" : "or";
            var clauses = new List<object>
            {
                BuildCompoundFilter(
                    positiveOperation,
                    recallPlan.PositiveTagIds.Select(tagId => (object)new object[] { "tag", "=", tagId }))
            };

            foreach (var negativeTagId in recallPlan.NegativeTagIds)
                clauses.Add(new object[] { "tag", "!=", negativeTagId });

            var filters = BuildCompoundFilter("and", clauses);

            for (var page = 1; page <= recallPlan.MaxPages; page++)
            {
                var pageResults = await SearchVndbCandidatesWithCacheAsync(
                    filters,
                    CandidateSearchPageSize,
                    recallPlan.Sort,
                    page,
                    cacheDocument);
                if (pageResults.Count == 0)
                    break;

                foreach (var visualNovel in pageResults)
                {
                    visualNovelsById.TryAdd(visualNovel.Id, visualNovel);
                    cacheDocument.Entries[visualNovel.Id] = visualNovel;
                }

                if (pageResults.Count < CandidateSearchPageSize)
                    break;
            }
        }
        if (visualNovelsById.Count < MinimumFallbackResultCount)
        {
            var broadFallback = await SearchBroadFallbackCandidatesAsync(profile, cacheDocument);
            foreach (var visualNovel in broadFallback)
            {
                if (!existingIds.Contains(visualNovel.Id))
                    visualNovelsById.TryAdd(visualNovel.Id, visualNovel);
            }
        }

        await _cacheService.SaveAsync(cacheDocument);

        return visualNovelsById.Values
            .Where(visualNovel => !existingIds.Contains(visualNovel.Id))
            .Where(visualNovel => !IsSkippedRecommendation(visualNovel.Id, feedbackById))
            .Select(visualNovel => BuildCandidate(visualNovel, profile, feedbackById))
            .ToList();
    }

    private async Task<List<RecommendationCandidate>> SearchExploreCandidatesAsync(
        RecommendationProfile profile,
        HashSet<string> existingIds,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var broadTags = profile.PositiveTagIds
            .Concat(profile.SecondaryPositiveTagIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        if (broadTags.Count == 0)
            return await SearchCandidatesAsync(profile, existingIds, feedbackById, cacheDocument);

        var recallPlans = new[]
        {
            new RecallPlan(broadTags.Take(12).ToList(), [], 7, "rating"),
            new RecallPlan(broadTags.Skip(4).Take(12).DefaultIfEmpty(broadTags[0]).ToList(), [], 5, "rating"),
            new RecallPlan(profile.PositiveTagIds.Take(4).ToList(), [], 3, "rating", requireAllPositiveTags: true),
        };

        var visualNovelsById = new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);
        foreach (var recallPlan in recallPlans.Where(plan => plan.PositiveTagIds.Count > 0))
        {
            var positiveOperation = recallPlan.RequireAllPositiveTags ? "and" : "or";
            var filters = BuildCompoundFilter(
                positiveOperation,
                recallPlan.PositiveTagIds.Select(tagId => (object)new object[] { "tag", "=", tagId }));

            for (var page = 1; page <= recallPlan.MaxPages; page++)
            {
                var pageResults = await SearchVndbCandidatesWithCacheAsync(
                    filters,
                    CandidateSearchPageSize,
                    recallPlan.Sort,
                    page,
                    cacheDocument);
                if (pageResults.Count == 0)
                    break;

                foreach (var visualNovel in pageResults)
                {
                    visualNovelsById.TryAdd(visualNovel.Id, visualNovel);
                    cacheDocument.Entries[visualNovel.Id] = visualNovel;
                }

                if (pageResults.Count < CandidateSearchPageSize)
                    break;
            }
        }

        await _cacheService.SaveAsync(cacheDocument);

        return visualNovelsById.Values
            .Where(visualNovel => !existingIds.Contains(visualNovel.Id))
            .Where(visualNovel => !IsSkippedRecommendation(visualNovel.Id, feedbackById))
            .Select(visualNovel => BuildCandidate(visualNovel, profile, feedbackById))
            .ToList();
    }

    private static bool IsSkippedRecommendation(
        string vndbId,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById)
    {
        return feedbackById.TryGetValue(vndbId, out var feedback)
               && feedback.LastNegativeAt.HasValue
               && feedback.NegativeSignal > 0;
    }

    private async Task<List<CachedVndbVisualNovel>> SearchBroadFallbackCandidatesAsync(
        RecommendationProfile profile,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var fallbackTags = profile.PositiveTagIds
            .Concat(profile.SecondaryPositiveTagIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (fallbackTags.Count == 0)
            return [];

        var filters = BuildCompoundFilter(
            "or",
            fallbackTags.Select(tagId => (object)new object[] { "tag", "=", tagId }));
        var fallbackResults = await SearchVndbCandidatesWithCacheAsync(
            filters,
            CandidateSearchPageSize,
            "rating",
            page: 1,
            cacheDocument);
        foreach (var visualNovel in fallbackResults)
            cacheDocument.Entries[visualNovel.Id] = visualNovel;

        return fallbackResults;
    }

    private async Task<List<CachedVndbVisualNovel>> SearchVndbCandidatesWithCacheAsync(
        object filters,
        int results,
        string sort,
        int page,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var cacheKey = BuildCandidateSearchCacheKey(filters, results, sort, page);
        if (cacheDocument.CandidateSearches.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAtUtc <= CandidateSearchCacheTtl)
        {
            var cachedResults = cached.VndbIds
                .Select(id => cacheDocument.Entries.TryGetValue(id, out var entry) ? entry : null)
                .Where(entry => entry != null)
                .Cast<CachedVndbVisualNovel>()
                .ToList();

            if (cachedResults.Count == cached.VndbIds.Count)
                return cachedResults;
        }

        var responseJson = await PostToVndbAsync(filters, results, DetailFields, sort, page);
        if (responseJson == null)
            return [];

        var pageResults = ParseVisualNovels(responseJson);
        cacheDocument.CandidateSearches[cacheKey] = new CachedVndbCandidateSearch
        {
            CachedAtUtc = DateTimeOffset.UtcNow,
            VndbIds = pageResults.Select(visualNovel => visualNovel.Id).ToList()
        };

        return pageResults;
    }

    private static string BuildCandidateSearchCacheKey(object filters, int results, string sort, int page)
    {
        var signature = JsonSerializer.Serialize(new { filters, results, sort, page });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes);
    }

    private async Task<string?> PostToVndbAsync(object filters, int results, string fields, string sort, int page = 1)
    {
        var response = await PostToVndbCoreAsync(filters, results, fields, sort, page);
        if (response != null || !string.Equals(fields, DetailFields, StringComparison.Ordinal))
            return response;

        return await PostToVndbCoreAsync(filters, results, DetailFieldsWithoutRank, sort, page);
    }

    private async Task<string?> PostToVndbCoreAsync(object filters, int results, string fields, string sort, int page = 1)
    {
        try
        {
            var payload = new { filters, fields, results, sort, reverse = true, page };
            using var request = new HttpRequestMessage(HttpMethod.Post, VndbApiUrl);
            request.Headers.Add("User-Agent", "Perelegans/0.2");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    private static List<CachedVndbVisualNovel> ParseVisualNovels(string responseJson)
    {
        var results = new List<CachedVndbVisualNovel>();
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("results", out var resultArray))
            return results;

        foreach (var item in resultArray.EnumerateArray())
        {
            var visualNovel = new CachedVndbVisualNovel
            {
                Id = item.TryGetProperty("id", out var idElement)
                    ? VndbIdUtilities.Normalize(idElement.GetString()) ?? string.Empty
                    : string.Empty,
                Title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty,
                OriginalTitle = item.TryGetProperty("alttitle", out var originalTitleElement) ? originalTitleElement.GetString() ?? string.Empty : string.Empty,
                CachedAtUtc = DateTimeOffset.UtcNow
            };

            if (visualNovel.Id.Length == 0)
                continue;

            if (item.TryGetProperty("released", out var releaseElement) &&
                TryParseFlexibleDate(releaseElement.GetString(), out var releaseDate))
            {
                visualNovel.ReleaseDate = releaseDate;
            }

            visualNovel.Rank = TryGetNullableInt32(item, "rank");
            visualNovel.Rating = TryGetNullableDouble(item, "rating");
            visualNovel.VoteCount = TryGetNullableInt32(item, "votecount");

            if (item.TryGetProperty("developers", out var developers) && developers.ValueKind == JsonValueKind.Array)
            {
                foreach (var developer in developers.EnumerateArray())
                {
                    if (!developer.TryGetProperty("name", out var nameElement))
                        continue;
                    var name = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        visualNovel.Developers.Add(name.Trim());
                }
            }

            if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    var tagId = tag.TryGetProperty("id", out var tagIdElement) ? tagIdElement.GetString() ?? string.Empty : string.Empty;
                    var tagName = tag.TryGetProperty("name", out var tagNameElement) ? tagNameElement.GetString() ?? string.Empty : string.Empty;
                    var rating = tag.TryGetProperty("rating", out var ratingElement) ? ratingElement.GetDouble() : 0d;
                    if (tagId.Length == 0 || tagName.Length == 0)
                        continue;

                    visualNovel.Tags.Add(new CachedVndbTag
                    {
                        Id = tagId,
                        Name = tagName,
                        Rating = rating
                    });
                }
            }

            if (item.TryGetProperty("extlinks", out var extlinks) && extlinks.ValueKind == JsonValueKind.Array)
            {
                foreach (var extlink in extlinks.EnumerateArray())
                {
                    var url = extlink.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                    if (url.Length == 0)
                        continue;

                    visualNovel.ExternalLinks.Add(new CachedVndbExternalLink
                    {
                        Url = url,
                        Label = extlink.TryGetProperty("label", out var labelElement) ? labelElement.GetString() ?? string.Empty : string.Empty,
                        Name = extlink.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty
                    });
                }
            }

            visualNovel.Developers = TagUtilities.Normalize(visualNovel.Developers);
            results.Add(visualNovel);
        }

        return results;
    }

    private static RecommendationProfile BuildProfile(
        IReadOnlyCollection<(Game Game, CachedVndbVisualNovel Metadata)> sourceGames,
        IReadOnlyDictionary<string, CachedVndbVisualNovel> feedbackMetadataById,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        IReadOnlyDictionary<string, CachedTagWeight> tagWeights,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases,
        TasteProfileSummary summary)
    {
        var tagScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var recentTagScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hardNegativeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var developerScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sourceProfiles = new List<SourceGameProfile>();
        double weightedYearSum = 0;
        double weightedYearTotal = 0;

        foreach (var (game, metadata) in sourceGames)
        {
            var weight = GetGameWeight(game);
            if (Math.Abs(weight) < 0.001)
                continue;

            var recentBias = ComputeRecentBias(game.AccessedDate);
            var sourcePositiveTags = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in metadata.Tags)
            {
                var semanticWeight = GetSemanticTagWeight(tag, tagWeights);
                var score = weight * tag.Rating * semanticWeight;
                foreach (var tagKey in GetProfileTagKeys(tag, tagAliases))
                {
                    tagScores[tagKey] = tagScores.TryGetValue(tagKey, out var current) ? current + score : score;
                    tagNames[tagKey] = GetCanonicalTagName(tag, tagAliases);

                    if (weight > 0)
                    {
                        sourcePositiveTags[tagKey] = sourcePositiveTags.TryGetValue(tagKey, out var positiveCurrent)
                            ? positiveCurrent + score
                            : score;
                        recentTagScores[tagKey] = recentTagScores.TryGetValue(tagKey, out var recentCurrent)
                            ? recentCurrent + score * recentBias
                            : score * recentBias;
                    }
                    else if (IsHardNegative(game))
                    {
                        hardNegativeScores[tagKey] = hardNegativeScores.TryGetValue(tagKey, out var negativeCurrent)
                            ? negativeCurrent + Math.Abs(score)
                            : Math.Abs(score);
                    }
                }
            }

            if (weight > 0)
            {
                foreach (var developer in metadata.Developers)
                {
                    developerScores[developer] = developerScores.TryGetValue(developer, out var current)
                        ? current + weight * recentBias
                        : weight * recentBias;
                }

                if (metadata.ReleaseDate.HasValue)
                {
                    weightedYearSum += metadata.ReleaseDate.Value.Year * weight * recentBias;
                    weightedYearTotal += weight * recentBias;
                }

                sourceProfiles.Add(new SourceGameProfile
                {
                    Title = !string.IsNullOrWhiteSpace(metadata.OriginalTitle) ? metadata.OriginalTitle : metadata.Title,
                    Developers = metadata.Developers,
                    ReleaseDate = metadata.ReleaseDate,
                    PositiveTagScores = sourcePositiveTags,
                    Weight = weight,
                    RecentBias = recentBias
                });
            }
        }

        var sourceVndbIds = sourceGames
            .Select(item => VndbIdUtilities.Normalize(item.Game.VndbId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (vndbId, feedback) in feedbackById)
        {
            var feedbackValue = feedback.PositiveSignal - feedback.NegativeSignal;
            if (Math.Abs(feedbackValue) < 0.001)
                continue;

            if (!sourceVndbIds.Contains(vndbId) &&
                feedbackMetadataById.TryGetValue(vndbId, out var feedbackMetadata))
            {
                ApplyFeedbackMetadataToProfile(
                    feedbackMetadata,
                    feedback,
                    feedbackValue,
                    tagScores,
                    recentTagScores,
                    hardNegativeScores,
                    tagNames,
                    developerScores,
                    sourceProfiles,
                    tagWeights,
                    tagAliases);
            }

            if (feedbackValue < 0)
            {
                var sourceGame = sourceGames.FirstOrDefault(item =>
                    string.Equals(VndbIdUtilities.Normalize(item.Game.VndbId), vndbId, StringComparison.OrdinalIgnoreCase));
                if (sourceGame.Metadata == null)
                    continue;

                foreach (var tag in sourceGame.Metadata.Tags)
                {
                    foreach (var tagKey in GetProfileTagKeys(tag, tagAliases))
                    {
                        hardNegativeScores[tagKey] = hardNegativeScores.TryGetValue(tagKey, out var current)
                            ? current + Math.Abs(feedbackValue) * tag.Rating
                            : Math.Abs(feedbackValue) * tag.Rating;
                        tagNames[tagKey] = GetCanonicalTagName(tag, tagAliases);
                    }
                }
            }
        }

        var positiveTags = tagScores
            .Where(item => item.Value > 0)
            .Where(item => !IsStructuralPreferenceTag(tagNames[item.Key]))
            .OrderByDescending(item => item.Value)
            .ToList();
        if (positiveTags.Count == 0)
        {
            positiveTags = tagScores
                .Where(item => item.Value > 0)
                .OrderByDescending(item => item.Value)
                .ToList();
        }

        var negativeTags = hardNegativeScores.OrderByDescending(item => item.Value).ToList();
        var softNegativeTags = tagScores.Where(item => item.Value < 0).OrderBy(item => item.Value).ToList();
        var topPositiveDevelopers = developerScores.Where(item => item.Value > 0).OrderByDescending(item => item.Value).ToList();

        var developerConcentration = ComputeConcentration(topPositiveDevelopers.Select(item => item.Value));
        var tagConcentration = ComputeConcentration(positiveTags.Take(8).Select(item => item.Value));
        var developerPreferenceStrength = developerConcentration <= 0 ? 0 : developerConcentration / Math.Max(tagConcentration, 0.0001);
        var recallPositiveTags = positiveTags
            .Where(item => IsVndbTagId(item.Key))
            .ToList();
        if (recallPositiveTags.Count == 0)
        {
            recallPositiveTags = tagScores
                .Where(item => item.Value > 0 && IsVndbTagId(item.Key))
                .OrderByDescending(item => item.Value)
                .ToList();
        }

        summary.TopPositiveTags = positiveTags.Take(6).Select(item => tagNames[item.Key]).ToList();
        summary.SecondaryPositiveTags = positiveTags.Skip(6).Take(6).Select(item => tagNames[item.Key]).ToList();
        summary.NegativeTags = negativeTags.Take(3).Select(item => tagNames[item.Key]).ToList();
        summary.SoftNegativeTags = softNegativeTags.Take(3).Select(item => tagNames[item.Key]).ToList();
        summary.PreferredDevelopers = topPositiveDevelopers.Take(3).Select(item => item.Key).ToList();
        summary.PreferredReleaseYear = weightedYearTotal > 0 ? weightedYearSum / weightedYearTotal : null;
        summary.PreferenceStyle = BuildPreferenceStyle(developerPreferenceStrength, topPositiveDevelopers.Count, positiveTags.Count);

        return new RecommendationProfile
        {
            TagScores = tagScores,
            RecentTagScores = recentTagScores,
            PositiveTagIds = recallPositiveTags.Take(6).Select(item => item.Key).ToList(),
            SecondaryPositiveTagIds = recallPositiveTags.Skip(6).Take(6).Select(item => item.Key).ToList(),
            NegativeTagIds = negativeTags.Take(3).Select(item => item.Key).ToList(),
            SoftNegativeTagIds = softNegativeTags.Take(3).Select(item => item.Key).ToList(),
            DeveloperScores = developerScores,
            PreferredDevelopers = summary.PreferredDevelopers,
            PreferredReleaseYear = summary.PreferredReleaseYear,
            MaxPositiveOverlap = positiveTags.Take(6).Sum(item => item.Value * 3d),
            MaxRecentOverlap = recentTagScores.Values.Where(value => value > 0).OrderByDescending(value => value).Take(6).Sum(value => value * 3d),
            SourceGames = sourceProfiles,
            TagAliases = tagAliases.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            DeveloperPreferenceWeight = developerPreferenceStrength >= 1.15
                ? Scoring.StrongDeveloperPreferenceWeight
                : Scoring.BalancedDeveloperPreferenceWeight
        };
    }

    private static void ApplyFeedbackMetadataToProfile(
        CachedVndbVisualNovel metadata,
        RecommendationFeedback feedback,
        double feedbackValue,
        Dictionary<string, double> tagScores,
        Dictionary<string, double> recentTagScores,
        Dictionary<string, double> hardNegativeScores,
        Dictionary<string, string> tagNames,
        Dictionary<string, double> developerScores,
        List<SourceGameProfile> sourceProfiles,
        IReadOnlyDictionary<string, CachedTagWeight> tagWeights,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases)
    {
        var recencyAnchor = feedback.LastPositiveAt ?? feedback.LastNegativeAt ?? feedback.UpdatedAt;
        var recentBias = ComputeRecentBias(recencyAnchor);
        var weight = Math.Clamp(feedbackValue, -Scoring.MaxFeedbackProfileSignal, Scoring.MaxFeedbackProfileSignal) *
                     Scoring.FeedbackProfileWeight;
        var sourcePositiveTags = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in metadata.Tags)
        {
            var semanticWeight = GetSemanticTagWeight(tag, tagWeights);
            var score = weight * tag.Rating * semanticWeight;
            foreach (var tagKey in GetProfileTagKeys(tag, tagAliases))
            {
                tagScores[tagKey] = tagScores.TryGetValue(tagKey, out var current) ? current + score : score;
                tagNames[tagKey] = GetCanonicalTagName(tag, tagAliases);

                if (weight > 0)
                {
                    sourcePositiveTags[tagKey] = sourcePositiveTags.TryGetValue(tagKey, out var positiveCurrent)
                        ? positiveCurrent + score
                        : score;
                    recentTagScores[tagKey] = recentTagScores.TryGetValue(tagKey, out var recentCurrent)
                        ? recentCurrent + score * recentBias
                        : score * recentBias;
                }
                else
                {
                    hardNegativeScores[tagKey] = hardNegativeScores.TryGetValue(tagKey, out var negativeCurrent)
                        ? negativeCurrent + Math.Abs(score)
                        : Math.Abs(score);
                }
            }
        }

        if (weight <= 0)
            return;

        foreach (var developer in metadata.Developers)
        {
            developerScores[developer] = developerScores.TryGetValue(developer, out var current)
                ? current + weight * recentBias
                : weight * recentBias;
        }

        sourceProfiles.Add(new SourceGameProfile
        {
            Title = !string.IsNullOrWhiteSpace(metadata.OriginalTitle) ? metadata.OriginalTitle : metadata.Title,
            Developers = metadata.Developers,
            ReleaseDate = metadata.ReleaseDate,
            PositiveTagScores = sourcePositiveTags,
            Weight = weight,
            RecentBias = recentBias
        });
    }

    private static RecommendationCandidate BuildCandidate(
        CachedVndbVisualNovel visualNovel,
        RecommendationProfile profile,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById)
    {
        var rawTagOverlap = 0d;
        var rawRecentOverlap = 0d;
        var matchedTags = new List<(string Name, double Score)>();
        var conflictingTags = new List<(string Name, double Score)>();

        foreach (var tag in visualNovel.Tags)
        {
            foreach (var tagKey in GetProfileTagKeys(tag, profile.TagAliases))
            {
                if (profile.TagScores.TryGetValue(tagKey, out var profileScore))
                {
                    rawTagOverlap += profileScore * tag.Rating;
                    if (profileScore > 0)
                        matchedTags.Add((tag.Name, profileScore * tag.Rating));
                }

                if (profile.RecentTagScores.TryGetValue(tagKey, out var recentScore))
                    rawRecentOverlap += recentScore * tag.Rating;

                if (profile.SoftNegativeTagIds.Contains(tagKey, StringComparer.OrdinalIgnoreCase) &&
                    profile.TagScores.TryGetValue(tagKey, out var negativeScore) &&
                    negativeScore < 0)
                {
                    conflictingTags.Add((tag.Name, negativeScore * tag.Rating));
                }
            }
        }

        var tagOverlapScore = profile.MaxPositiveOverlap <= 0 ? 0 : Math.Clamp(rawTagOverlap / profile.MaxPositiveOverlap, -1, 1);
        var recentAlignment = profile.MaxRecentOverlap <= 0 ? 0 : Math.Clamp(rawRecentOverlap / profile.MaxRecentOverlap, 0, 1);
        var developerBonus = ComputeDeveloperBonus(visualNovel.Developers, profile.DeveloperScores);
        var yearAffinity = ComputeYearAffinity(visualNovel.ReleaseDate, profile.PreferredReleaseYear);
        var feedbackAffinity = ComputeFeedbackAffinity(visualNovel.Id, feedbackById);
        var sourceMatches = BuildSourceMatches(visualNovel, profile.SourceGames, profile.TagAliases);

        var recommendationScore = tagOverlapScore
            + profile.DeveloperPreferenceWeight * developerBonus
            + Scoring.YearAffinityWeight * yearAffinity
            + Scoring.FeedbackAffinityWeight * feedbackAffinity
            + Scoring.RecentAlignmentWeight * recentAlignment;

        return new RecommendationCandidate
        {
            VndbId = visualNovel.Id,
            Title = visualNovel.Title,
            OriginalTitle = visualNovel.OriginalTitle,
            Brand = string.Join(", ", visualNovel.Developers),
            ReleaseDate = visualNovel.ReleaseDate,
            VndbUrl = VndbIdUtilities.ToWebUrl(visualNovel.Id),
            OfficialWebsite = SelectOfficialWebsite(visualNovel.ExternalLinks),
            VndbRank = visualNovel.Rank,
            VndbRating = visualNovel.Rating,
            VndbVoteCount = visualNovel.VoteCount,
            Tags = TagUtilities.Normalize(visualNovel.Tags.Select(tag => tag.Name)),
            MatchingTags = matchedTags.OrderByDescending(item => item.Score).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
            ConflictingTags = conflictingTags.OrderBy(item => item.Score).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
            MatchingDevelopers = visualNovel.Developers
                .Where(profile.DeveloperScores.ContainsKey)
                .OrderByDescending(developer => profile.DeveloperScores[developer])
                .Take(2)
                .ToList(),
            SourceMatches = sourceMatches,
            RecommendationScore = recommendationScore,
            TagOverlapScore = tagOverlapScore,
            DeveloperBonus = developerBonus,
            YearAffinity = yearAffinity,
            FeedbackAffinity = feedbackAffinity,
            RecencyAlignment = recentAlignment,
            ScoreBreakdown = BuildScoreBreakdown(tagOverlapScore, developerBonus, yearAffinity, feedbackAffinity, recentAlignment),
            SourceMatchSummary = sourceMatches.Count == 0
                ? string.Empty
                : string.Join(", ", sourceMatches.Select(match => match.Title)),
            IsAlreadyInLibrary = false
        };
    }

    private static bool IsRelevantCandidate(RecommendationCandidate candidate)
    {
        if (candidate.RecommendationScore <= 0.16)
            return false;

        return candidate.TagOverlapScore >= 0.10
               || candidate.DeveloperBonus >= 0.35
               || candidate.SourceMatches.Count > 0
               || candidate.FeedbackAffinity > 0.15;
    }

    public async Task EnrichCandidatesWithBangumiRatingsAsync(IReadOnlyList<RecommendationCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var bangumiService = new BangumiService(_httpClient);
        var cacheDocument = await _cacheService.LoadAsync();
        var cacheLock = new object();
        var queryCache = new ConcurrentDictionary<string, Lazy<Task<List<MetadataResult>>>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(4);

        var tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync();
            try
            {
                var match = await WithTimeout(
                    FindBestBangumiMatchAsync(
                        bangumiService,
                        candidate,
                        cacheDocument,
                        cacheLock,
                        queryCache),
                    BangumiRequestTimeout);

                if (match == null)
                {
                    candidate.ExternalRatingScore = NormalizeRating(candidate.VndbRating);
                    return;
                }

                candidate.BangumiId = match.SourceId;
                candidate.BangumiRating = match.Rating;
                candidate.BangumiRank = match.Rank;
                candidate.BangumiVoteCount = match.VoteCount;
                candidate.ExternalRatingScore = ComputeExternalRatingScore(match.Rating, candidate.VndbRating);
            }
            catch
            {
                candidate.ExternalRatingScore = NormalizeRating(candidate.VndbRating);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        await _cacheService.SaveAsync(cacheDocument);
    }

    private static async Task<MetadataResult?> FindBestBangumiMatchAsync(
        BangumiService bangumiService,
        RecommendationCandidate candidate,
        VndbRecommendationCacheDocument cacheDocument,
        object cacheLock,
        ConcurrentDictionary<string, Lazy<Task<List<MetadataResult>>>> queryCache)
    {
        var results = new Dictionary<string, MetadataResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in GetBangumiSearchQueries(candidate))
        {
            var queryResults = await queryCache.GetOrAdd(
                query,
                key => new Lazy<Task<List<MetadataResult>>>(
                    () => SearchBangumiWithCacheAsync(bangumiService, key, cacheDocument, cacheLock))).Value;

            foreach (var result in queryResults)
            {
                if (!string.IsNullOrWhiteSpace(result.SourceId))
                    results.TryAdd(result.SourceId, result);
            }
        }

        if (results.Count == 0)
            return null;

        var normalizedTitle = NormalizeTitleForMatch(candidate.Title);
        var normalizedOriginalTitle = NormalizeTitleForMatch(candidate.OriginalTitle);

        var ranked = results.Values
            .Select(result => new
            {
                Result = result,
                TitleScore = CalculateTitleMatchScore(result, normalizedTitle, normalizedOriginalTitle),
                YearCompatible = IsYearCompatible(candidate.ReleaseDate, result.ReleaseDate, Scoring.BangumiYearTolerance),
                HasDetails = !string.IsNullOrWhiteSpace(result.Brand) || result.Tags.Count > 0
            })
            .Where(item => item.YearCompatible)
            .Where(item => item.TitleScore >= (item.HasDetails ? Scoring.BangumiDetailMatchThreshold : Scoring.BangumiExactMatchThreshold))
            .OrderByDescending(item => item.TitleScore)
            .ThenByDescending(item => item.HasDetails ? 1 : 0)
            .ThenByDescending(item => item.Result.Rating.HasValue ? 1 : 0)
            .ThenByDescending(item => item.Result.VoteCount ?? -1)
            .ThenByDescending(item => item.Result.Rating ?? -1)
            .ToList();

        var best = ranked.FirstOrDefault()?.Result;
        if (best == null || string.IsNullOrWhiteSpace(best.SourceId) || !string.IsNullOrWhiteSpace(best.Brand) || best.Tags.Count > 0)
            return best;

        var detailed = await WithTimeout(
            bangumiService.GetByIdAsync(best.SourceId),
            BangumiRequestTimeout);
        return detailed ?? best;
    }

    private async Task<CachedVndbVisualNovel?> FindBestVndbMatchAsync(
        Game game,
        MetadataResult bangumiMetadata,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var results = new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in GetCrossSourceSearchQueries(game, bangumiMetadata))
        {
            var filters = new object[] { "search", "=", query };
            var responseJson = await PostToVndbAsync(filters, 10, DetailFields, "searchrank");
            if (responseJson == null)
                continue;

            foreach (var visualNovel in ParseVisualNovels(responseJson))
            {
                cacheDocument.Entries[visualNovel.Id] = visualNovel;
                results.TryAdd(visualNovel.Id, visualNovel);
            }
        }

        if (results.Count == 0)
            return null;

        var normalizedTitle = NormalizeTitleForMatch(game.Title);
        var normalizedOriginalTitle = NormalizeTitleForMatch(bangumiMetadata.OriginalTitle);
        return results.Values
            .Select(visualNovel => new
            {
                VisualNovel = visualNovel,
                TitleScore = CalculateTitleMatchScore(ToMetadataResult(visualNovel), normalizedTitle, normalizedOriginalTitle),
                YearCompatible = IsYearCompatible(game.ReleaseDate ?? bangumiMetadata.ReleaseDate, visualNovel.ReleaseDate, Scoring.BangumiYearTolerance)
            })
            .Where(item => item.YearCompatible)
            .Where(item => item.TitleScore >= Scoring.CrossSourceMatchThreshold)
            .OrderByDescending(item => item.TitleScore)
            .ThenByDescending(item => item.VisualNovel.Rating ?? 0)
            .ThenByDescending(item => item.VisualNovel.VoteCount ?? 0)
            .Select(item => item.VisualNovel)
            .FirstOrDefault();
    }

    private static RecommendationCandidate BuildCandidateForCrossSourceMatch(CachedVndbVisualNovel metadata)
    {
        return new RecommendationCandidate
        {
            VndbId = metadata.Id,
            Title = metadata.Title,
            OriginalTitle = metadata.OriginalTitle,
            Brand = string.Join(", ", metadata.Developers),
            ReleaseDate = metadata.ReleaseDate,
            VndbRating = metadata.Rating,
            VndbRank = metadata.Rank,
            VndbVoteCount = metadata.VoteCount,
            Tags = TagUtilities.Normalize(metadata.Tags.Select(tag => tag.Name))
        };
    }

    private static MetadataResult ToMetadataResult(CachedVndbVisualNovel visualNovel)
    {
        return new MetadataResult
        {
            Source = "VNDB",
            SourceId = visualNovel.Id,
            Title = visualNovel.Title,
            OriginalTitle = visualNovel.OriginalTitle,
            Brand = string.Join(", ", visualNovel.Developers),
            ReleaseDate = visualNovel.ReleaseDate,
            Rating = visualNovel.Rating,
            Rank = visualNovel.Rank,
            VoteCount = visualNovel.VoteCount,
            Tags = TagUtilities.Normalize(visualNovel.Tags.Select(tag => tag.Name))
        };
    }

    private static List<string> GetCrossSourceSearchQueries(Game game, MetadataResult metadata)
    {
        var queries = new List<string>();
        AddBangumiSearchQuery(queries, game.Title);
        AddBangumiSearchQuery(queries, metadata.Title);
        AddBangumiSearchQuery(queries, metadata.OriginalTitle);
        foreach (var title in queries.ToList())
        {
            AddBangumiSearchQuery(queries, StripEditionSuffix(title));
            AddBangumiSearchQuery(queries, StripBracketedText(title));
        }

        return queries
            .Where(query => query.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static void MergeGameMetadataFromResult(Game game, MetadataResult metadata)
    {
        if (string.IsNullOrWhiteSpace(game.Brand) && !string.IsNullOrWhiteSpace(metadata.Brand))
            game.Brand = metadata.Brand;
        if (!game.ReleaseDate.HasValue && metadata.ReleaseDate.HasValue)
            game.ReleaseDate = metadata.ReleaseDate;
        if (string.IsNullOrWhiteSpace(game.Tags) && metadata.Tags.Count > 0)
            game.Tags = string.Join(Environment.NewLine, metadata.Tags);
        if (string.IsNullOrWhiteSpace(game.CoverImageUrl) && !string.IsNullOrWhiteSpace(metadata.ImageUrl))
            game.CoverImageUrl = metadata.ImageUrl;
    }

    private static void MergeGameMetadataFromVndb(Game game, CachedVndbVisualNovel metadata)
    {
        if (string.IsNullOrWhiteSpace(game.Brand) && metadata.Developers.Count > 0)
            game.Brand = string.Join(", ", metadata.Developers);
        if (!game.ReleaseDate.HasValue && metadata.ReleaseDate.HasValue)
            game.ReleaseDate = metadata.ReleaseDate;
        if (string.IsNullOrWhiteSpace(game.Tags) && metadata.Tags.Count > 0)
            game.Tags = string.Join(Environment.NewLine, TagUtilities.Normalize(metadata.Tags.Select(tag => tag.Name)));
        if (string.IsNullOrWhiteSpace(game.OfficialWebsite))
            game.OfficialWebsite = SelectOfficialWebsite(metadata.ExternalLinks);
    }

    private static async Task<List<MetadataResult>> SearchBangumiWithCacheAsync(
        BangumiService bangumiService,
        string query,
        VndbRecommendationCacheDocument cacheDocument,
        object cacheLock)
    {
        var cacheKey = BuildBangumiSearchCacheKey(query);
        lock (cacheLock)
        {
            if (cacheDocument.BangumiSearches.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAtUtc <= BangumiSearchCacheTtl)
            {
                return cached.Results;
            }
        }

        var results = await WithTimeout(
            bangumiService.SearchAsync(query, includeDetails: false),
            BangumiRequestTimeout,
            new List<MetadataResult>());
        lock (cacheLock)
        {
            cacheDocument.BangumiSearches[cacheKey] = new CachedBangumiSearch
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                Results = results
            };
        }

        return results;
    }

    private static List<string> GetBangumiSearchQueries(RecommendationCandidate candidate)
    {
        var queries = new List<string>();
        AddBangumiSearchQuery(queries, candidate.DisplayTitle);
        AddBangumiSearchQuery(queries, candidate.Title);
        AddBangumiSearchQuery(queries, candidate.OriginalTitle);

        foreach (var title in queries.ToList())
        {
            AddBangumiSearchQuery(queries, StripEditionSuffix(title));
            AddBangumiSearchQuery(queries, StripBracketedText(title));
        }

        return queries
            .Where(query => query.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static async Task<T?> WithTimeout<T>(Task<T?> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            return default;

        return await task;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, T fallback)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            return fallback;

        return await task;
    }

    private static void AddBangumiSearchQuery(List<string> queries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var trimmed = query.Trim();
        if (trimmed.Length > 0)
            queries.Add(trimmed);
    }

    private static string BuildBangumiSearchCacheKey(string query)
    {
        return NormalizeTitleForMatch(query);
    }

    private static string StripBracketedText(string title)
    {
        var builder = new StringBuilder(title.Length);
        var depth = 0;
        foreach (var ch in title)
        {
            if (ch is '(' or '（' or '[' or '［' or '【')
            {
                depth++;
                continue;
            }

            if (ch is ')' or '）' or ']' or '］' or '】')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
                builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static string StripEditionSuffix(string title)
    {
        var separators = new[] { " - ", " / ", " ～", " ~ " };
        foreach (var separator in separators)
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index > 2)
                return title[..index].Trim();
        }

        return title.Trim();
    }

    private static bool IsTitleMatch(MetadataResult result, string normalizedTitle, string normalizedOriginalTitle)
    {
        var resultTitle = NormalizeTitleForMatch(result.Title);
        var resultOriginalTitle = NormalizeTitleForMatch(result.OriginalTitle);

        return (!string.IsNullOrWhiteSpace(normalizedTitle) &&
                (string.Equals(resultTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(resultOriginalTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase)))
               || (!string.IsNullOrWhiteSpace(normalizedOriginalTitle) &&
                    (string.Equals(resultTitle, normalizedOriginalTitle, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(resultOriginalTitle, normalizedOriginalTitle, StringComparison.OrdinalIgnoreCase)));
    }

    private static double CalculateTitleMatchScore(MetadataResult result, string normalizedTitle, string normalizedOriginalTitle)
    {
        var resultTitle = NormalizeTitleForMatch(result.Title);
        var resultOriginalTitle = NormalizeTitleForMatch(result.OriginalTitle);
        var candidateTitles = new[] { normalizedTitle, normalizedOriginalTitle }
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resultTitles = new[] { resultTitle, resultOriginalTitle }
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateTitles.Count == 0 || resultTitles.Count == 0)
            return 0;

        if (resultTitles.Any(resultValue => candidateTitles.Any(candidateValue =>
                string.Equals(resultValue, candidateValue, StringComparison.OrdinalIgnoreCase))))
        {
            return 1;
        }

        return resultTitles
            .SelectMany(resultValue => candidateTitles.Select(candidateValue => ComputeStringSimilarity(resultValue, candidateValue)))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool IsYearCompatible(DateTime? candidateReleaseDate, DateTime? resultReleaseDate, int toleranceYears)
    {
        if (!candidateReleaseDate.HasValue || !resultReleaseDate.HasValue)
            return true;

        return Math.Abs(candidateReleaseDate.Value.Year - resultReleaseDate.Value.Year) <= toleranceYears;
    }

    private static string NormalizeTitleForMatch(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var chars = title
            .Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
            .Select(char.ToUpperInvariant);
        return new string(chars.ToArray());
    }

    private static double ComputeStringSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return 0;

        if (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
            right.Contains(left, StringComparison.OrdinalIgnoreCase))
        {
            var shorter = Math.Min(left.Length, right.Length);
            var longer = Math.Max(left.Length, right.Length);
            return longer == 0 ? 0 : (double)shorter / longer;
        }

        var distance = ComputeLevenshteinDistance(left, right);
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static IEnumerable<string> GetProfileTagKeys(
        CachedVndbTag tag,
        IReadOnlyDictionary<string, CachedTagAlias>? tagAliases = null)
    {
        yield return tag.Id;

        var nameKey = BuildBangumiTagKey(tag.Name);
        if (!string.Equals(nameKey, tag.Id, StringComparison.OrdinalIgnoreCase))
            yield return nameKey;

        if (tagAliases == null || !TryGetTagAlias(tag, tagAliases, out var alias))
            yield break;

        var canonicalKey = BuildBangumiTagKey(alias.CanonicalTag);
        if (!string.IsNullOrWhiteSpace(canonicalKey))
            yield return canonicalKey;

        foreach (var aliasName in alias.Aliases)
        {
            var aliasKey = BuildBangumiTagKey(aliasName);
            if (!string.IsNullOrWhiteSpace(aliasKey))
                yield return aliasKey;
        }
    }

    private static string GetCanonicalTagName(
        CachedVndbTag tag,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases)
    {
        return TryGetTagAlias(tag, tagAliases, out var alias) &&
               !string.IsNullOrWhiteSpace(alias.CanonicalTag)
            ? alias.CanonicalTag
            : tag.Name;
    }

    private static bool TryGetTagAlias(
        CachedVndbTag tag,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases,
        out CachedTagAlias alias)
    {
        return tagAliases.TryGetValue(tag.Id, out alias!) ||
               tagAliases.TryGetValue(tag.Name, out alias!) ||
               tagAliases.TryGetValue(BuildTagWeightKey(tag.Name), out alias!);
    }

    private static string BuildBangumiTagKey(string tagName)
    {
        var normalized = NormalizeTitleForMatch(tagName);
        return string.IsNullOrWhiteSpace(normalized)
            ? $"tag:{tagName.Trim().ToUpperInvariant()}"
            : $"tag:{normalized}";
    }

    private static bool IsVndbTagId(string tagId)
    {
        return tagId.Length > 1 && tagId[0] == 'g' && tagId.Skip(1).All(char.IsDigit);
    }

    private static double? ComputeExternalRatingScore(double? bangumiRating, double? vndbRating)
    {
        var normalizedBangumi = NormalizeRating(bangumiRating);
        var normalizedVndb = NormalizeRating(vndbRating);

        if (normalizedBangumi.HasValue && normalizedVndb.HasValue)
            return normalizedBangumi.GetValueOrDefault() * 0.5 + normalizedVndb.GetValueOrDefault() * 0.5;
        if (normalizedBangumi.HasValue)
            return normalizedBangumi.GetValueOrDefault();
        if (normalizedVndb.HasValue)
            return normalizedVndb.GetValueOrDefault();

        return null;
    }

    private static double? NormalizeRating(double? rating)
    {
        if (!rating.HasValue || rating.Value <= 0)
            return null;

        var value = rating.Value > 10 ? rating.Value / 100d : rating.Value / 10d;
        return Math.Clamp(value, 0, 1);
    }

    private static string BuildScoreBreakdown(
        double tagOverlapScore,
        double developerBonus,
        double yearAffinity,
        double feedbackAffinity,
        double recentAlignment)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Tags {0:F2} | Dev {1:F2} | Era {2:F2} | Feedback {3:F2} | Recent {4:F2}",
            tagOverlapScore,
            developerBonus,
            yearAffinity,
            feedbackAffinity,
            recentAlignment);
    }

    private static List<RecommendationSourceMatch> BuildSourceMatches(
        CachedVndbVisualNovel visualNovel,
        IReadOnlyCollection<SourceGameProfile> sourceGames,
        IReadOnlyDictionary<string, CachedTagAlias> tagAliases)
    {
        return sourceGames
            .Select(source =>
            {
                var tagScore = visualNovel.Tags
                    .Sum(tag => GetProfileTagKeys(tag, tagAliases)
                        .Where(tagKey => source.PositiveTagScores.TryGetValue(tagKey, out _))
                        .Sum(tagKey => source.PositiveTagScores[tagKey] * tag.Rating));
                var developerScore = visualNovel.Developers
                    .Intersect(source.Developers, StringComparer.OrdinalIgnoreCase)
                    .Any() ? 0.8 : 0;
                var yearScore = ComputeYearAffinity(visualNovel.ReleaseDate, source.ReleaseDate?.Year);
                var score = tagScore + developerScore + 0.25 * yearScore + 0.15 * source.RecentBias;

                return new RecommendationSourceMatch
                {
                    Title = source.Title,
                    Score = score
                };
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .Take(3)
            .ToList();
    }

    private static double GetGameWeight(Game game)
    {
        var hours = game.Playtime.TotalHours;
        var statusWeight = game.Status switch
        {
            GameStatus.Completed => 1.15,
            GameStatus.Playing => 0.7,
            GameStatus.Dropped when hours < 2 => -1.15,
            GameStatus.Dropped when hours < 12 => -0.7,
            GameStatus.Dropped => -0.35,
            GameStatus.Planned => 0.1,
            _ => 0.0
        };

        var engagementMultiplier = 1 + Math.Min(hours, 40) / 40d;
        var recencyMultiplier = 0.6 + 0.7 * ComputeRecentBias(game.AccessedDate);
        return statusWeight * engagementMultiplier * recencyMultiplier;
    }

    private static bool IsHardNegative(Game game)
    {
        return game.Status == GameStatus.Dropped && game.Playtime.TotalHours < 12;
    }

    private static double ComputeRecentBias(DateTime accessedDate)
    {
        var days = Math.Max((DateTime.Now - accessedDate).TotalDays, 0);
        return Math.Exp(-days / 180d);
    }

    private static double ComputeDeveloperBonus(IReadOnlyCollection<string> developers, IReadOnlyDictionary<string, double> scores)
    {
        if (developers.Count == 0 || scores.Count == 0)
            return 0;

        var maxScore = scores.Values.Where(score => score > 0).DefaultIfEmpty(0).Max();
        if (maxScore <= 0)
            return 0;

        var bestScore = developers
            .Where(developer => scores.TryGetValue(developer, out var score) && score > 0)
            .Select(developer => scores[developer])
            .DefaultIfEmpty(0)
            .Max();
        return bestScore <= 0 ? 0 : Math.Clamp(bestScore / maxScore, 0, 1);
    }

    private static double ComputeYearAffinity(DateTime? releaseDate, double? preferredReleaseYear)
    {
        if (!releaseDate.HasValue || !preferredReleaseYear.HasValue)
            return 0;

        var yearDistance = Math.Abs(releaseDate.Value.Year - preferredReleaseYear.Value);
        return Math.Clamp(1 - yearDistance / 18d, 0, 1);
    }

    private static double ComputeYearAffinity(DateTime? releaseDate, int? preferredReleaseYear)
    {
        return preferredReleaseYear.HasValue
            ? ComputeYearAffinity(releaseDate, (double?)preferredReleaseYear.Value)
            : 0;
    }

    private static double ComputeFeedbackAffinity(
        string vndbId,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById)
    {
        if (!feedbackById.TryGetValue(vndbId, out var feedback))
            return 0;

        var net = feedback.PositiveSignal - feedback.NegativeSignal;
        var total = feedback.PositiveSignal + feedback.NegativeSignal;
        if (Math.Abs(net) < 0.001 || total <= 0)
            return 0;

        var recencyAnchor = feedback.LastPositiveAt ?? feedback.LastNegativeAt ?? feedback.UpdatedAt;
        var recencyWeight = 0.7 + 0.3 * ComputeRecentBias(recencyAnchor);
        return Math.Clamp((net / (total + 0.75)) * recencyWeight, -1, 1);
    }

    private static double ComputeConcentration(IEnumerable<double> values)
    {
        var entries = values.Where(value => value > 0).ToList();
        if (entries.Count == 0)
            return 0;

        var total = entries.Sum();
        if (total <= 0)
            return 0;

        return entries.Max() / total;
    }

    private static double GetSemanticTagWeight(
        CachedVndbTag tag,
        IReadOnlyDictionary<string, CachedTagWeight> tagWeights)
    {
        if (tagWeights.TryGetValue(tag.Name, out var cached) ||
            tagWeights.TryGetValue(BuildTagWeightKey(tag.Name), out cached) ||
            tagWeights.TryGetValue(tag.Id, out cached))
        {
            return Math.Clamp(cached.Weight * Math.Clamp(cached.Confidence, 0.35, 1.0), 0.2, 1.8);
        }

        return GetHeuristicTagWeight(tag.Name);
    }

    public static string BuildTagWeightKey(string tagName)
    {
        var normalized = NormalizeTitleForMatch(tagName);
        return string.IsNullOrWhiteSpace(normalized)
            ? tagName.Trim().ToUpperInvariant()
            : normalized;
    }

    private static double GetHeuristicTagWeight(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return 0.7;

        if (IsStructuralPreferenceTag(tagName))
            return 0.35;

        if (tagName.Contains("Protagonist", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Heroine", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Ending", StringComparison.OrdinalIgnoreCase))
        {
            return 0.55;
        }

        if (tagName.Contains("Sexual", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Violence", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Gore", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Rape", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Incest", StringComparison.OrdinalIgnoreCase))
        {
            return 1.35;
        }

        if (tagName.Contains("Romance", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Drama", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Mystery", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Comedy", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Horror", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Fantasy", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Sci-fi", StringComparison.OrdinalIgnoreCase))
        {
            return 1.2;
        }

        return 1.0;
    }

    private static string BuildPreferenceStyle(double developerPreferenceStrength, int developerCount, int positiveTagCount)
    {
        if (developerPreferenceStrength >= 1.35 && developerCount > 0)
            return TranslationService.Instance["Rec_ProfileStyleDeveloperStrong"];
        if (developerPreferenceStrength >= 0.95 && developerCount > 0)
            return TranslationService.Instance["Rec_ProfileStyleDeveloperBalanced"];
        if (positiveTagCount >= 8)
            return TranslationService.Instance["Rec_ProfileStyleTagDiverse"];

        return TranslationService.Instance["Rec_ProfileStyleTagFocused"];
    }

    private static bool IsStructuralPreferenceTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return true;

        return tagName.Contains("Protagonist with", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("Playing Order", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("One True End", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("Route", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("Voice Acting", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("Heroine", StringComparison.OrdinalIgnoreCase)
               || tagName.Contains("ADV", StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildCompoundFilter(string operation, IEnumerable<object> clauses)
    {
        var clauseList = clauses.ToList();
        if (clauseList.Count == 0)
            return new object[] { "id", "!=", "v0" };
        if (clauseList.Count == 1)
            return clauseList[0];

        var filter = new object[clauseList.Count + 1];
        filter[0] = operation;
        for (var i = 0; i < clauseList.Count; i++)
            filter[i + 1] = clauseList[i];
        return filter;
    }

    private static string? SelectOfficialWebsite(IReadOnlyCollection<CachedVndbExternalLink> links)
    {
        static bool MatchesOfficialKeyword(string value)
        {
            return value.Contains("official", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("homepage", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("website", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("site", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("公式", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("官网", StringComparison.OrdinalIgnoreCase);
        }

        var preferred = links.FirstOrDefault(link => MatchesOfficialKeyword(link.Label) || MatchesOfficialKeyword(link.Name));
        return preferred?.Url ?? links.FirstOrDefault()?.Url;
    }

    private static bool TryParseFlexibleDate(string? value, out DateTime date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = default;
            return false;
        }

        return DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM", "yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
               || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static int? TryGetNullableInt32(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? TryGetNullableDouble(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private sealed class RecommendationProfile
    {
        public Dictionary<string, double> TagScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> RecentTagScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PositiveTagIds { get; init; } = [];
        public List<string> SecondaryPositiveTagIds { get; init; } = [];
        public List<string> NegativeTagIds { get; init; } = [];
        public List<string> SoftNegativeTagIds { get; init; } = [];
        public Dictionary<string, double> DeveloperScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PreferredDevelopers { get; init; } = [];
        public double? PreferredReleaseYear { get; init; }
        public double MaxPositiveOverlap { get; init; }
        public double MaxRecentOverlap { get; init; }
        public List<SourceGameProfile> SourceGames { get; init; } = [];
        public IReadOnlyDictionary<string, CachedTagAlias> TagAliases { get; init; } = new Dictionary<string, CachedTagAlias>(StringComparer.OrdinalIgnoreCase);
        public double DeveloperPreferenceWeight { get; init; }
    }

    private sealed class RecommendationScoringOptions
    {
        public double StrongDeveloperPreferenceWeight { get; init; } = 0.35;
        public double BalancedDeveloperPreferenceWeight { get; init; } = 0.22;
        public double YearAffinityWeight { get; init; } = 0.12;
        public double FeedbackAffinityWeight { get; init; } = 0.18;
        public double RecentAlignmentWeight { get; init; } = 0.12;
        public double FeedbackProfileWeight { get; init; } = 0.65;
        public double MaxFeedbackProfileSignal { get; init; } = 2.5;
        public double BangumiExactMatchThreshold { get; init; } = 0.88;
        public double BangumiDetailMatchThreshold { get; init; } = 0.72;
        public double CrossSourceMatchThreshold { get; init; } = 0.78;
        public int BangumiYearTolerance { get; init; } = 2;
        public double ProfileSelectionWeight { get; init; } = 0.68;
        public double ExternalSelectionWeight { get; init; } = 0.32;
        public int BlockingBangumiEnrichmentLimit { get; init; } = 32;
    }

    private sealed record RecommendationProfileContext(
        TasteProfileSummary ProfileSummary,
        RecommendationProfile? Profile,
        HashSet<string> LibraryIds,
        IReadOnlyDictionary<string, RecommendationFeedback> FeedbackById,
        VndbRecommendationCacheDocument CacheDocument);

    private sealed class SourceGameProfile
    {
        public string Title { get; init; } = string.Empty;
        public IReadOnlyCollection<string> Developers { get; init; } = [];
        public DateTime? ReleaseDate { get; init; }
        public Dictionary<string, double> PositiveTagScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public double Weight { get; init; }
        public double RecentBias { get; init; }
    }

    private sealed class RecallPlan(
        List<string> positiveTagIds,
        List<string> negativeTagIds,
        int maxPages,
        string sort,
        bool requireAllPositiveTags = false)
    {
        public List<string> PositiveTagIds { get; } = positiveTagIds;
        public List<string> NegativeTagIds { get; } = negativeTagIds;
        public int MaxPages { get; } = maxPages;
        public string Sort { get; } = sort;
        public bool RequireAllPositiveTags { get; } = requireAllPositiveTags;
    }
}
