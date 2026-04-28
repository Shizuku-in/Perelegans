using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
    private const int FinalRecommendationLimit = 24;
    private const int MinimumFallbackResultCount = 12;
    private readonly DatabaseService _dbService;
    private readonly HttpClient _httpClient;
    private readonly VndbRecommendationCacheService _cacheService;

    public RecommendationService(DatabaseService dbService, HttpClient httpClient, VndbRecommendationCacheService cacheService)
    {
        _dbService = dbService;
        _httpClient = httpClient;
        _cacheService = cacheService;
    }

    public async Task<RecommendationResult> GetRecommendationsAsync()
    {
        var games = await _dbService.GetAllGamesAsync();
        var result = new RecommendationResult
        {
            ProfileSummary = new TasteProfileSummary
            {
                TotalLibraryGames = games.Count,
                CompletedGames = games.Count(game => game.Status == GameStatus.Completed),
                DroppedGames = games.Count(game => game.Status == GameStatus.Dropped)
            }
        };

        result.ProfileSummary.CompletionRate = games.Count == 0
            ? 0
            : (double)result.ProfileSummary.CompletedGames / games.Count;
        result.ProfileSummary.AveragePlaytimeHours = games.Count == 0
            ? 0
            : games.Average(game => game.Playtime.TotalHours);
        result.ProfileSummary.AverageCompletedHours = games
            .Where(game => game.Status == GameStatus.Completed)
            .Select(game => game.Playtime.TotalHours)
            .DefaultIfEmpty(0)
            .Average();
        result.ProfileSummary.AverageDroppedHours = games
            .Where(game => game.Status == GameStatus.Dropped)
            .Select(game => game.Playtime.TotalHours)
            .DefaultIfEmpty(0)
            .Average();

        var libraryIds = games
            .Select(game => VndbIdUtilities.Normalize(game.VndbId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cacheDocument = await _cacheService.LoadAsync();
        var metadataById = await GetVisualNovelsByIdsAsync(libraryIds, cacheDocument);
        var bangumiMetadataByGameId = await GetBangumiMetadataByGameIdAsync(games);
        var feedbackById = await _dbService.GetRecommendationFeedbackMapAsync();

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

        result.ProfileSummary.EligibleLibraryGames = profiledGames.Count;
        if (profiledGames.Count < 3)
            return result;

        var profile = BuildProfile(profiledGames, feedbackById, result.ProfileSummary);
        if (profile.PositiveTagIds.Count == 0)
            return result;

        var rankedCandidates = (await SearchCandidatesAsync(profile, libraryIds, feedbackById, cacheDocument))
            .OrderByDescending(candidate => candidate.RecommendationScore)
            .ThenByDescending(candidate => candidate.TagOverlapScore)
            .ThenByDescending(candidate => candidate.FeedbackAffinity)
            .ThenByDescending(candidate => candidate.DeveloperBonus)
            .ThenByDescending(candidate => candidate.YearAffinity)
            .ThenByDescending(candidate => candidate.ReleaseDate ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Candidates = rankedCandidates
            .Where(IsRelevantCandidate)
            .Take(FinalRecommendationLimit)
            .ToList();

        if (result.Candidates.Count == 0)
        {
            result.Candidates = rankedCandidates
                .Where(candidate => candidate.RecommendationScore > 0)
                .Take(MinimumFallbackResultCount)
                .ToList();
        }

        return result;
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

    private async Task<List<RecommendationCandidate>> SearchCandidatesAsync(
        RecommendationProfile profile,
        HashSet<string> existingIds,
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var recallPlans = new[]
        {
            new RecallPlan(profile.PositiveTagIds.Take(6).ToList(), profile.NegativeTagIds.Take(3).ToList(), 5),
            new RecallPlan(
                profile.SecondaryPositiveTagIds.Count > 0 ? profile.SecondaryPositiveTagIds.Take(8).ToList() : profile.PositiveTagIds.Take(8).ToList(),
                profile.NegativeTagIds.Take(1).ToList(),
                3)
        };

        var visualNovelsById = new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);

        foreach (var recallPlan in recallPlans.Where(plan => plan.PositiveTagIds.Count > 0))
        {
            var clauses = new List<object>
            {
                BuildCompoundFilter(
                    "or",
                    recallPlan.PositiveTagIds.Select(tagId => (object)new object[] { "tag", "=", tagId }))
            };

            foreach (var negativeTagId in recallPlan.NegativeTagIds)
                clauses.Add(new object[] { "tag", "!=", negativeTagId });

            var filters = BuildCompoundFilter("and", clauses);

            for (var page = 1; page <= recallPlan.MaxPages; page++)
            {
                var responseJson = await PostToVndbAsync(filters, CandidateSearchPageSize, DetailFields, "rating", page);
                if (responseJson == null)
                    break;

                var pageResults = ParseVisualNovels(responseJson);
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
            .Select(visualNovel => BuildCandidate(visualNovel, profile, feedbackById))
            .ToList();
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
        var responseJson = await PostToVndbAsync(filters, CandidateSearchPageSize, DetailFields, "rating");
        if (responseJson == null)
            return [];

        var fallbackResults = ParseVisualNovels(responseJson);
        foreach (var visualNovel in fallbackResults)
            cacheDocument.Entries[visualNovel.Id] = visualNovel;

        return fallbackResults;
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
        IReadOnlyDictionary<string, RecommendationFeedback> feedbackById,
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
                var score = weight * tag.Rating;
                tagScores[tag.Id] = tagScores.TryGetValue(tag.Id, out var current) ? current + score : score;
                tagNames[tag.Id] = tag.Name;

                if (weight > 0)
                {
                    sourcePositiveTags[tag.Id] = sourcePositiveTags.TryGetValue(tag.Id, out var positiveCurrent)
                        ? positiveCurrent + score
                        : score;
                    recentTagScores[tag.Id] = recentTagScores.TryGetValue(tag.Id, out var recentCurrent)
                        ? recentCurrent + score * recentBias
                        : score * recentBias;
                }
                else if (IsHardNegative(game))
                {
                    hardNegativeScores[tag.Id] = hardNegativeScores.TryGetValue(tag.Id, out var negativeCurrent)
                        ? negativeCurrent + Math.Abs(score)
                        : Math.Abs(score);
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

        foreach (var (vndbId, feedback) in feedbackById)
        {
            var feedbackValue = feedback.PositiveSignal - feedback.NegativeSignal;
            if (Math.Abs(feedbackValue) < 0.001)
                continue;

            if (feedbackValue < 0)
            {
                var sourceGame = sourceGames.FirstOrDefault(item =>
                    string.Equals(VndbIdUtilities.Normalize(item.Game.VndbId), vndbId, StringComparison.OrdinalIgnoreCase));
                if (sourceGame.Metadata == null)
                    continue;

                foreach (var tag in sourceGame.Metadata.Tags)
                {
                    hardNegativeScores[tag.Id] = hardNegativeScores.TryGetValue(tag.Id, out var current)
                        ? current + Math.Abs(feedbackValue) * tag.Rating
                        : Math.Abs(feedbackValue) * tag.Rating;
                    tagNames[tag.Id] = tag.Name;
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
        summary.PreferenceStyle = developerPreferenceStrength >= 1.15 ? "Developer-led" : "Tag-led";

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
            DeveloperPreferenceWeight = developerPreferenceStrength >= 1.15 ? 0.35 : 0.22
        };
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
            foreach (var tagKey in GetProfileTagKeys(tag))
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
        var sourceMatches = BuildSourceMatches(visualNovel, profile.SourceGames);

        var recommendationScore = tagOverlapScore
            + profile.DeveloperPreferenceWeight * developerBonus
            + 0.12 * yearAffinity
            + 0.18 * feedbackAffinity
            + 0.12 * recentAlignment;

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
        var queryCache = new ConcurrentDictionary<string, Lazy<Task<MetadataResult?>>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(4);

        var tasks = candidates.Select(async candidate =>
        {
            await throttler.WaitAsync();
            try
            {
                var query = GetBangumiSearchQuery(candidate);
                var match = string.IsNullOrWhiteSpace(query)
                    ? null
                    : await queryCache.GetOrAdd(
                        query,
                        key => new Lazy<Task<MetadataResult?>>(
                            () => FindBestBangumiMatchAsync(bangumiService, candidate, key))).Value;

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
    }

    private static async Task<MetadataResult?> FindBestBangumiMatchAsync(
        BangumiService bangumiService,
        RecommendationCandidate candidate,
        string query)
    {
        var results = await bangumiService.SearchAsync(query, includeDetails: false);
        if (results.Count == 0)
            return null;

        var normalizedTitle = NormalizeTitleForMatch(candidate.Title);
        var normalizedOriginalTitle = NormalizeTitleForMatch(candidate.OriginalTitle);

        return results
            .OrderByDescending(result => IsTitleMatch(result, normalizedTitle, normalizedOriginalTitle) ? 1 : 0)
            .ThenByDescending(result => result.Rating.HasValue ? 1 : 0)
            .ThenByDescending(result => result.VoteCount ?? -1)
            .ThenByDescending(result => result.Rating ?? -1)
            .FirstOrDefault();
    }

    private static string GetBangumiSearchQuery(RecommendationCandidate candidate)
    {
        var query = candidate.DisplayTitle;
        if (string.IsNullOrWhiteSpace(query))
            query = candidate.Title;
        return query.Trim();
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

    private static string NormalizeTitleForMatch(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var chars = title
            .Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
            .Select(char.ToUpperInvariant);
        return new string(chars.ToArray());
    }

    private static IEnumerable<string> GetProfileTagKeys(CachedVndbTag tag)
    {
        yield return tag.Id;

        var nameKey = BuildBangumiTagKey(tag.Name);
        if (!string.Equals(nameKey, tag.Id, StringComparison.OrdinalIgnoreCase))
            yield return nameKey;
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
            return normalizedBangumi.GetValueOrDefault() * 0.75 + normalizedVndb.GetValueOrDefault() * 0.25;
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
        IReadOnlyCollection<SourceGameProfile> sourceGames)
    {
        return sourceGames
            .Select(source =>
            {
                var tagScore = visualNovel.Tags
                    .Sum(tag => GetProfileTagKeys(tag)
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
        public double DeveloperPreferenceWeight { get; init; }
    }

    private sealed class SourceGameProfile
    {
        public string Title { get; init; } = string.Empty;
        public IReadOnlyCollection<string> Developers { get; init; } = [];
        public DateTime? ReleaseDate { get; init; }
        public Dictionary<string, double> PositiveTagScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public double Weight { get; init; }
        public double RecentBias { get; init; }
    }

    private sealed class RecallPlan(List<string> positiveTagIds, List<string> negativeTagIds, int maxPages)
    {
        public List<string> PositiveTagIds { get; } = positiveTagIds;
        public List<string> NegativeTagIds { get; } = negativeTagIds;
        public int MaxPages { get; } = maxPages;
    }
}
