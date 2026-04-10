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

    public async Task<AiRecommendationResult> ExplainAsync(
        TasteProfileSummary profileSummary,
        IReadOnlyList<RecommendationCandidate> candidates)
    {
        var result = new AiRecommendationResult();

        if (!IsConfigured || candidates.Count == 0)
            return result;

        if (!Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            result.ErrorMessage = "AI base URL is invalid.";
            return result;
        }

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
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                result.ErrorMessage = $"AI request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {TruncateForDisplay(errorBody)}";
                return result;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var content = ExtractAssistantContent(responseJson);
            if (string.IsNullOrWhiteSpace(content))
            {
                result.ErrorMessage = "AI response did not contain readable content.";
                return result;
            }

            if (!TryExtractJsonPayload(content, out var sanitizedJson))
            {
                result.ErrorMessage = $"AI response was not valid JSON. Raw content: {TruncateForDisplay(content)}";
                return result;
            }

            using var document = JsonDocument.Parse(sanitizedJson);

            if (document.RootElement.TryGetProperty("explanations", out var explanationArray) &&
                explanationArray.ValueKind == JsonValueKind.Array)
            {
                result.Explanations = ParseExplanations(explanationArray);
                if (!result.HasExplanations)
                    result.ErrorMessage = "AI JSON parsed successfully, but no matching explanations were returned.";
                return result;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                result.Explanations = ParseExplanations(document.RootElement);
                if (!result.HasExplanations)
                    result.ErrorMessage = "AI JSON parsed successfully, but the explanation array was empty.";
                return result;
            }

            result.ErrorMessage = "AI JSON did not contain an 'explanations' array.";
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"AI parsing failed: {ex.Message}";
            return result;
        }
    }

    private static string? ExtractAssistantContent(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);

        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message))
                return null;

            if (message.TryGetProperty("content", out var content))
                return ExtractTextContent(content);

            return null;
        }

        if (document.RootElement.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array &&
            output.GetArrayLength() > 0)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
                    continue;

                var text = ExtractTextFromContentArray(contentArray);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private static string? ExtractTextContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => ExtractTextFromContentArray(content),
            JsonValueKind.Object when content.TryGetProperty("text", out var textElement) => ExtractTextContent(textElement),
            _ => null
        };
    }

    private static string? ExtractTextFromContentArray(JsonElement contentArray)
    {
        var parts = contentArray
            .EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();

                if (item.ValueKind != JsonValueKind.Object)
                    return null;

                if (item.TryGetProperty("text", out var textElement))
                {
                    if (textElement.ValueKind == JsonValueKind.String)
                        return textElement.GetString();

                    if (textElement.ValueKind == JsonValueKind.Object &&
                        textElement.TryGetProperty("value", out var valueElement) &&
                        valueElement.ValueKind == JsonValueKind.String)
                    {
                        return valueElement.GetString();
                    }
                }

                if (item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();

                if (item.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
                    return outputText.GetString();

                return null;
            })
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n", parts);
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

    private static bool TryExtractJsonPayload(string content, out string json)
    {
        var stripped = StripMarkdownCodeFence(content);

        if (LooksLikeJson(stripped))
        {
            json = stripped;
            return true;
        }

        var firstObject = stripped.IndexOf('{');
        var firstArray = stripped.IndexOf('[');
        var start = firstObject < 0
            ? firstArray
            : firstArray < 0 ? firstObject : Math.Min(firstObject, firstArray);

        if (start < 0)
        {
            json = string.Empty;
            return false;
        }

        for (var end = stripped.Length; end > start; end--)
        {
            var candidate = stripped[start..end].Trim();
            if (!LooksLikeJson(candidate))
                continue;

            try
            {
                using var _ = JsonDocument.Parse(candidate);
                json = candidate;
                return true;
            }
            catch
            {
            }
        }

        json = string.Empty;
        return false;
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            || (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal));
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

    private static string TruncateForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 180 ? singleLine : $"{singleLine[..180]}...";
    }
}
