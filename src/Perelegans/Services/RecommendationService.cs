using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class RecommendationService
{
    private const string VndbApiUrl = "https://api.vndb.org/kana/vn";
    private const string DetailFields = "id, title, alttitle, released, developers.name, tags.id, tags.name, tags.rating, extlinks.url, extlinks.label, extlinks.name";
    private const int CacheTtlDays = 7;
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
                TotalLibraryGames = games.Count
            }
        };

        var libraryIds = games
            .Select(game => VndbIdUtilities.Normalize(game.VndbId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cacheDocument = await _cacheService.LoadAsync();
        var metadataById = await GetVisualNovelsByIdsAsync(libraryIds, cacheDocument);

        var profiledGames = games
            .Select(game => new { Game = game, VndbId = VndbIdUtilities.Normalize(game.VndbId) })
            .Where(item => item.VndbId != null && metadataById.ContainsKey(item.VndbId))
            .Select(item => (item.Game, Metadata: metadataById[item.VndbId!]))
            .ToList();

        result.ProfileSummary.EligibleLibraryGames = profiledGames.Count;
        if (profiledGames.Count < 3)
            return result;

        var profile = BuildProfile(profiledGames);
        result.ProfileSummary.TopPositiveTags = profile.TopPositiveTagNames;
        result.ProfileSummary.NegativeTags = profile.NegativeTagNames;
        result.ProfileSummary.PreferredDevelopers = profile.PreferredDevelopers;
        result.ProfileSummary.PreferredReleaseYear = profile.PreferredReleaseYear;

        if (profile.PositiveTagIds.Count == 0)
            return result;

        result.Candidates = (await SearchCandidatesAsync(profile, libraryIds, cacheDocument))
            .Where(candidate => candidate.RecommendationScore > 0)
            .OrderByDescending(candidate => candidate.RecommendationScore)
            .ThenByDescending(candidate => candidate.TagOverlapScore)
            .Take(24)
            .ToList();

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

    private async Task<List<RecommendationCandidate>> SearchCandidatesAsync(
        RecommendationProfile profile,
        HashSet<string> existingIds,
        VndbRecommendationCacheDocument cacheDocument)
    {
        var clauses = new List<object>
        {
            BuildCompoundFilter(
                "or",
                profile.PositiveTagIds.Select(tagId => (object)new object[] { "tag", "=", tagId }))
        };

        foreach (var negativeTagId in profile.NegativeTagIds)
            clauses.Add(new object[] { "tag", "!=", negativeTagId });

        var filters = BuildCompoundFilter("and", clauses);
        var responseJson = await PostToVndbAsync(filters, 100, DetailFields, "rating");
        if (responseJson == null)
            return [];

        var visualNovels = ParseVisualNovels(responseJson);
        foreach (var visualNovel in visualNovels)
            cacheDocument.Entries[visualNovel.Id] = visualNovel;

        await _cacheService.SaveAsync(cacheDocument);

        return visualNovels
            .Where(visualNovel => !existingIds.Contains(visualNovel.Id))
            .Select(visualNovel => BuildCandidate(visualNovel, profile, existingIds))
            .ToList();
    }

    private async Task<string?> PostToVndbAsync(object filters, int results, string fields, string sort)
    {
        try
        {
            var payload = new { filters, fields, results, sort, reverse = true };
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

    private static RecommendationProfile BuildProfile(IReadOnlyCollection<(Game Game, CachedVndbVisualNovel Metadata)> sourceGames)
    {
        var tagScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var developerScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double weightedYearSum = 0;
        double weightedYearTotal = 0;

        foreach (var (game, metadata) in sourceGames)
        {
            var weight = GetGameWeight(game);
            if (Math.Abs(weight) < 0.001)
                continue;

            foreach (var tag in metadata.Tags)
            {
                tagScores[tag.Id] = tagScores.TryGetValue(tag.Id, out var current) ? current + weight * tag.Rating : weight * tag.Rating;
                tagNames[tag.Id] = tag.Name;
            }

            if (weight > 0)
            {
                foreach (var developer in metadata.Developers)
                {
                    developerScores[developer] = developerScores.TryGetValue(developer, out var current) ? current + weight : weight;
                }

                if (metadata.ReleaseDate.HasValue)
                {
                    weightedYearSum += metadata.ReleaseDate.Value.Year * weight;
                    weightedYearTotal += weight;
                }
            }
        }

        var positiveTags = tagScores.Where(item => item.Value > 0).OrderByDescending(item => item.Value).ToList();
        var negativeTags = tagScores.Where(item => item.Value < 0).OrderBy(item => item.Value).ToList();

        return new RecommendationProfile
        {
            TagScores = tagScores,
            PositiveTagIds = positiveTags.Take(6).Select(item => item.Key).ToList(),
            NegativeTagIds = negativeTags.Take(3).Select(item => item.Key).ToList(),
            TopPositiveTagNames = positiveTags.Take(6).Select(item => tagNames[item.Key]).ToList(),
            NegativeTagNames = negativeTags.Take(3).Select(item => tagNames[item.Key]).ToList(),
            DeveloperScores = developerScores,
            PreferredDevelopers = developerScores.Where(item => item.Value > 0).OrderByDescending(item => item.Value).Take(3).Select(item => item.Key).ToList(),
            PreferredReleaseYear = weightedYearTotal > 0 ? weightedYearSum / weightedYearTotal : null,
            MaxPositiveOverlap = positiveTags.Take(6).Sum(item => item.Value * 3d)
        };
    }

    private static RecommendationCandidate BuildCandidate(
        CachedVndbVisualNovel visualNovel,
        RecommendationProfile profile,
        HashSet<string> existingIds)
    {
        var rawTagOverlap = 0d;
        var matchedTags = new List<(string Name, double Score)>();
        var conflictingTags = new List<(string Name, double Score)>();

        foreach (var tag in visualNovel.Tags)
        {
            if (!profile.TagScores.TryGetValue(tag.Id, out var profileScore))
                continue;

            rawTagOverlap += profileScore * tag.Rating;
            if (profileScore > 0)
                matchedTags.Add((tag.Name, profileScore * tag.Rating));
            else if (profileScore < 0)
                conflictingTags.Add((tag.Name, profileScore * tag.Rating));
        }

        var tagOverlapScore = profile.MaxPositiveOverlap <= 0 ? 0 : Math.Clamp(rawTagOverlap / profile.MaxPositiveOverlap, -1, 1);
        var developerBonus = ComputeDeveloperBonus(visualNovel.Developers, profile.DeveloperScores);
        var yearAffinity = ComputeYearAffinity(visualNovel.ReleaseDate, profile.PreferredReleaseYear);

        return new RecommendationCandidate
        {
            VndbId = visualNovel.Id,
            Title = visualNovel.Title,
            OriginalTitle = visualNovel.OriginalTitle,
            Brand = string.Join(", ", visualNovel.Developers),
            ReleaseDate = visualNovel.ReleaseDate,
            VndbUrl = VndbIdUtilities.ToWebUrl(visualNovel.Id),
            OfficialWebsite = SelectOfficialWebsite(visualNovel.ExternalLinks),
            Tags = TagUtilities.Normalize(visualNovel.Tags.Select(tag => tag.Name)),
            MatchingTags = matchedTags.OrderByDescending(item => item.Score).Take(3).Select(item => item.Name).ToList(),
            ConflictingTags = conflictingTags.OrderBy(item => item.Score).Take(2).Select(item => item.Name).ToList(),
            MatchingDevelopers = visualNovel.Developers.Where(profile.DeveloperScores.ContainsKey).OrderByDescending(developer => profile.DeveloperScores[developer]).Take(2).ToList(),
            RecommendationScore = tagOverlapScore + 0.25 * developerBonus + 0.10 * yearAffinity,
            TagOverlapScore = tagOverlapScore,
            DeveloperBonus = developerBonus,
            YearAffinity = yearAffinity,
            IsAlreadyInLibrary = existingIds.Contains(visualNovel.Id)
        };
    }

    private static double GetGameWeight(Game game)
    {
        var statusWeight = game.Status switch
        {
            GameStatus.Completed => 1.0,
            GameStatus.Playing => 0.6,
            GameStatus.Dropped => -0.8,
            GameStatus.Planned => 0.0,
            _ => 0.0
        };

        return statusWeight * (1 + Math.Min(game.Playtime.TotalHours, 40) / 40d);
    }

    private static double ComputeDeveloperBonus(IReadOnlyCollection<string> developers, IReadOnlyDictionary<string, double> scores)
    {
        if (developers.Count == 0 || scores.Count == 0)
            return 0;

        var maxScore = scores.Values.Where(score => score > 0).DefaultIfEmpty(0).Max();
        if (maxScore <= 0)
            return 0;

        var bestScore = developers.Where(developer => scores.TryGetValue(developer, out var score) && score > 0).Select(developer => scores[developer]).DefaultIfEmpty(0).Max();
        return bestScore <= 0 ? 0 : Math.Clamp(bestScore / maxScore, 0, 1);
    }

    private static double ComputeYearAffinity(DateTime? releaseDate, double? preferredReleaseYear)
    {
        if (!releaseDate.HasValue || !preferredReleaseYear.HasValue)
            return 0;

        var yearDistance = Math.Abs(releaseDate.Value.Year - preferredReleaseYear.Value);
        return Math.Clamp(1 - yearDistance / 20d, 0, 1);
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

    private sealed class RecommendationProfile
    {
        public Dictionary<string, double> TagScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PositiveTagIds { get; init; } = [];
        public List<string> NegativeTagIds { get; init; } = [];
        public List<string> TopPositiveTagNames { get; init; } = [];
        public List<string> NegativeTagNames { get; init; } = [];
        public Dictionary<string, double> DeveloperScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> PreferredDevelopers { get; init; } = [];
        public double? PreferredReleaseYear { get; init; }
        public double MaxPositiveOverlap { get; init; }
    }
}
