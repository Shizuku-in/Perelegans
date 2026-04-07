using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class AiRecommendationService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public AiRecommendationService(HttpClient httpClient, SettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settingsService.Settings.AiApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_settingsService.Settings.AiApiKey)
        && !string.IsNullOrWhiteSpace(_settingsService.Settings.AiModel);

    public async Task<IReadOnlyList<AiRecommendationExplanation>> ExplainAsync(
        TasteProfileSummary profileSummary,
        IReadOnlyList<RecommendationCandidate> candidates)
    {
        if (!IsConfigured || candidates.Count == 0)
            return [];

        if (!Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return [];

        try
        {
            var payload = new
            {
                language = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language),
                summary = new
                {
                    totalGames = profileSummary.TotalLibraryGames,
                    eligibleGames = profileSummary.EligibleLibraryGames,
                    topTags = profileSummary.TopPositiveTags,
                    avoidTags = profileSummary.NegativeTags,
                    preferredDevelopers = profileSummary.PreferredDevelopers,
                    preferredReleaseYear = profileSummary.PreferredReleaseYear
                },
                candidates = candidates.Select(candidate => new
                {
                    candidateId = candidate.VndbId,
                    title = candidate.DisplayTitle,
                    brand = candidate.Brand,
                    releaseDate = candidate.ReleaseDate?.ToString("yyyy-MM-dd"),
                    matchingTags = candidate.MatchingTags,
                    conflictingTags = candidate.ConflictingTags,
                    matchingDevelopers = candidate.MatchingDevelopers,
                    yearAffinity = candidate.YearAffinity
                })
            };

            var requestBody = new
            {
                model = _settingsService.Settings.AiModel.Trim(),
                temperature = 0.3,
                max_tokens = 900,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You explain visual novel recommendations. Return JSON only. Keep each reason concise and practical."
                    },
                    new
                    {
                        role = "user",
                        content =
                            "Return a JSON object with an 'explanations' array. " +
                            "Each item must contain candidateId, reason, matchingTags, and caution. " +
                            "matchingTags must be an array of short strings already present in the candidate data. " +
                            "reason should be one or two short sentences. caution can be empty. " +
                            $"Data: {JsonSerializer.Serialize(payload)}"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settingsService.Settings.AiApiKey.Trim());
            request.Headers.Add("User-Agent", "Perelegans/0.2");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return [];

            var responseJson = await response.Content.ReadAsStringAsync();
            var content = ExtractAssistantContent(responseJson);
            if (string.IsNullOrWhiteSpace(content))
                return [];

            var sanitizedJson = StripMarkdownCodeFence(content);
            using var document = JsonDocument.Parse(sanitizedJson);

            if (document.RootElement.TryGetProperty("explanations", out var explanationArray) &&
                explanationArray.ValueKind == JsonValueKind.Array)
            {
                return ParseExplanations(explanationArray);
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return ParseExplanations(document.RootElement);

            return [];
        }
        catch
        {
            return [];
        }
    }

    private static string? ExtractAssistantContent(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message))
            return null;

        if (message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return null;
    }

    private static List<AiRecommendationExplanation> ParseExplanations(JsonElement explanationArray)
    {
        var explanations = new List<AiRecommendationExplanation>();
        foreach (var item in explanationArray.EnumerateArray())
        {
            if (!item.TryGetProperty("candidateId", out var candidateIdElement))
                continue;

            var candidateId = VndbIdUtilities.Normalize(candidateIdElement.GetString());
            if (string.IsNullOrWhiteSpace(candidateId))
                continue;

            var explanation = new AiRecommendationExplanation
            {
                CandidateId = candidateId,
                Reason = item.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString() ?? string.Empty
                    : string.Empty,
                Caution = item.TryGetProperty("caution", out var cautionElement)
                    ? cautionElement.GetString() ?? string.Empty
                    : string.Empty
            };

            if (item.TryGetProperty("matchingTags", out var matchingTagsElement) &&
                matchingTagsElement.ValueKind == JsonValueKind.Array)
            {
                explanation.MatchingTags = matchingTagsElement
                    .EnumerateArray()
                    .Where(tag => tag.ValueKind == JsonValueKind.String)
                    .Select(tag => tag.GetString())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            explanations.Add(explanation);
        }

        return explanations;
    }

    private static string StripMarkdownCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            return trimmed.Trim('`');

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineEnd)
            return trimmed[(firstLineEnd + 1)..];

        return trimmed[(firstLineEnd + 1)..lastFence].Trim();
    }
}
