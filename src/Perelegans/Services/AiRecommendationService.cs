using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class AiRecommendationService
{
    private const string AnthropicVersion = "2023-06-01";
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
            var provider = ResolveProvider(baseUri);
            var payload = new
            {
                language = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language),
                summary = new
                {
                    totalGames = profileSummary.TotalLibraryGames,
                    eligibleGames = profileSummary.EligibleLibraryGames,
                    completedGames = profileSummary.CompletedGames,
                    droppedGames = profileSummary.DroppedGames,
                    completionRate = profileSummary.CompletionRate,
                    averagePlaytimeHours = profileSummary.AveragePlaytimeHours,
                    averageCompletedHours = profileSummary.AverageCompletedHours,
                    averageDroppedHours = profileSummary.AverageDroppedHours,
                    topTags = profileSummary.TopPositiveTags,
                    secondaryTags = profileSummary.SecondaryPositiveTags,
                    avoidTags = profileSummary.NegativeTags,
                    softAvoidTags = profileSummary.SoftNegativeTags,
                    preferredDevelopers = profileSummary.PreferredDevelopers,
                    preferredReleaseYear = profileSummary.PreferredReleaseYear,
                    preferenceStyle = profileSummary.PreferenceStyle
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
                    yearAffinity = candidate.YearAffinity,
                    localScore = candidate.RecommendationScore,
                    vndbRank = candidate.VndbRank,
                    vndbRating = candidate.VndbRating,
                    vndbVoteCount = candidate.VndbVoteCount,
                    bangumiId = candidate.BangumiId,
                    bangumiRank = candidate.BangumiRank,
                    bangumiRating = candidate.BangumiRating,
                    bangumiVoteCount = candidate.BangumiVoteCount,
                    externalRatingScore = candidate.ExternalRatingScore,
                    scoreBreakdown = candidate.ScoreBreakdown,
                    similarLibraryTitles = candidate.SourceMatches.Select(match => match.Title).ToList()
                })
            };

            using var request = BuildRequest(provider, baseUri, payload);
            using var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"AI request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {TruncateForDisplay(responseBody)}";
                return result;
            }

            string? content;
            try
            {
                content = ExtractAssistantContent(responseBody, provider);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"AI parsing failed: {ex.Message}. Raw response: {TruncateForDisplay(responseBody)}";
                return result;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                result.ErrorMessage = "AI response did not contain readable content.";
                return result;
            }

            if (!TryExtractJsonPayload(content, out var sanitizedJson))
            {
                if (TryParseLooseExplanations(content, out var fallbackExplanations))
                {
                    result.Explanations = fallbackExplanations;
                    return result;
                }

                result.ErrorMessage = $"AI response was not valid JSON. Raw content: {TruncateForDisplay(content)}";
                return result;
            }

            using var document = JsonDocument.Parse(sanitizedJson);

            if (document.RootElement.TryGetProperty("explanations", out var explanationArray) &&
                explanationArray.ValueKind == JsonValueKind.Array)
            {
                result.Explanations = ParseExplanations(explanationArray);
                result.UserProfileSummary = ParseUserProfileSummary(document.RootElement);
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

            if (TryParseLooseExplanations(content, out var fallbackExplanationsFromStructuredResponse))
            {
                result.Explanations = fallbackExplanationsFromStructuredResponse;
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

    private HttpRequestMessage BuildRequest(AiProvider provider, Uri baseUri, object payload)
    {
        var model = _settingsService.Settings.AiModel.Trim();
        var apiKey = _settingsService.Settings.AiApiKey.Trim();
        var userPrompt =
            "Return a JSON object with a short 'userProfileSummary' string and an 'explanations' array. " +
            "Each item must contain candidateId, reason, matchingTags, caution, and sellingPoint. " +
            "Each item may include affinityScore from 0.0 to 1.0, where 1.0 means very aligned with the generated user taste profile. Use the full range and make the scores discriminative, not clustered. " +
            "matchingTags must be an array of short strings already present in the candidate data. " +
            "reason should be one or two short sentences. caution can be empty. " +
            "When Bangumi data exists, consider bangumiRating, bangumiRank, and externalRatingScore; externalRatingScore is Bangumi 75% and VNDB 25%. " +
            "sellingPoint should be a short label like 'Tag match', 'Developer pick', 'Recent-taste fit', or 'Contrast pick'. " +
            $"Data: {JsonSerializer.Serialize(payload)}";

        if (provider == AiProvider.Anthropic)
        {
            var anthropicBody = new
            {
                model,
                max_tokens = 900,
                temperature = 0.3,
                system = "You explain visual novel recommendations. Return JSON only. Keep each reason concise and practical.",
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt
                    }
                }
            };

            using var _ = JsonDocument.Parse(JsonSerializer.Serialize(anthropicBody));
            var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUri, provider));
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Headers.Add("User-Agent", "Perelegans/0.2");
            request.Content = new StringContent(JsonSerializer.Serialize(anthropicBody), Encoding.UTF8, "application/json");
            return request;
        }

        var openAiCompatibleBody = new
        {
            model,
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
                    content = userPrompt
                }
            }
        };

        var openAiRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUri, provider));
        openAiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        openAiRequest.Headers.Add("User-Agent", "Perelegans/0.2");
        if (provider == AiProvider.OpenRouter)
        {
            openAiRequest.Headers.Add("HTTP-Referer", "https://github.com/Shizuku-in/Perelegans");
            openAiRequest.Headers.Add("X-Title", "Perelegans");
        }

        openAiRequest.Content = new StringContent(JsonSerializer.Serialize(openAiCompatibleBody), Encoding.UTF8, "application/json");
        return openAiRequest;
    }

    private AiProvider ResolveProvider(Uri baseUri)
    {
        var configuredProvider = _settingsService.Settings.AiProvider;
        if (configuredProvider != AiProvider.Auto)
            return configuredProvider;

        var host = baseUri.Host.ToLowerInvariant();
        if (host.Contains("anthropic"))
            return AiProvider.Anthropic;
        if (host.Contains("openrouter"))
            return AiProvider.OpenRouter;

        return AiProvider.OpenAI;
    }

    private static Uri BuildEndpoint(Uri baseUri, AiProvider provider)
    {
        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);

        return provider == AiProvider.Anthropic
            ? new Uri(normalizedBase, "v1/messages")
            : new Uri(normalizedBase, "chat/completions");
    }

    private static string? ExtractAssistantContent(string responseJson, AiProvider provider)
    {
        using var document = JsonDocument.Parse(responseJson);

        if (provider == AiProvider.Anthropic)
            return ExtractAnthropicContent(document.RootElement);

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

    private static string? ExtractAnthropicContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            return null;

        return ExtractTextFromContentArray(contentArray);
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

                if (item.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    !item.TryGetProperty("text", out _))
                {
                    return null;
                }

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

            if (item.TryGetProperty("sellingPoint", out var sellingPointElement))
            {
                explanation.SellingPoint = sellingPointElement.GetString() ?? string.Empty;
            }

            if (item.TryGetProperty("affinityScore", out var affinityElement))
                explanation.AffinityScore = TryReadClampedScore(affinityElement);

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

    private static string ParseUserProfileSummary(JsonElement root)
    {
        if (root.TryGetProperty("userProfileSummary", out var summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.String)
        {
            return summaryElement.GetString()?.Trim() ?? string.Empty;
        }

        if (root.TryGetProperty("profileSummary", out var profileElement) &&
            profileElement.ValueKind == JsonValueKind.String)
        {
            return profileElement.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double? TryReadClampedScore(JsonElement element)
    {
        double value;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return Math.Clamp(value, 0, 1);

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return Math.Clamp(value, 0, 1);
        }

        return null;
    }

    private static bool TryParseLooseExplanations(string content, out List<AiRecommendationExplanation> explanations)
    {
        explanations = [];
        var normalized = NormalizeLooseExtractionText(content);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var idMatches = Regex.Matches(
            normalized,
            @"candidateId\s*""?\s*:\s*""?(v\d+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (idMatches.Count == 0)
            return false;

        for (var i = 0; i < idMatches.Count; i++)
        {
            var match = idMatches[i];
            var candidateId = VndbIdUtilities.Normalize(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(candidateId))
                continue;

            var startIndex = match.Index;
            var endIndex = i + 1 < idMatches.Count ? idMatches[i + 1].Index : normalized.Length;
            var block = normalized[startIndex..endIndex];

            var explanation = new AiRecommendationExplanation
            {
                CandidateId = candidateId,
                Reason = ExtractLooseJsonStringValue(block, "reason"),
                Caution = ExtractLooseJsonStringValue(block, "caution"),
                SellingPoint = ExtractLooseJsonStringValue(block, "sellingPoint")
            };

            explanation.MatchingTags = ExtractLooseStringArray(block, "matchingTags");
            explanations.Add(explanation);
        }

        explanations = explanations
            .GroupBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(item => !string.IsNullOrWhiteSpace(item.Reason)
                || item.MatchingTags.Count > 0
                || !string.IsNullOrWhiteSpace(item.Caution))
            .ToList();

        return explanations.Count > 0;
    }

    private static string NormalizeLooseExtractionText(string content)
    {
        var normalized = NormalizeJsonishText(content)
            .Replace("\\\"", "\"")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");

        var unwrapped = TryUnwrapJsonString(normalized);
        return string.IsNullOrWhiteSpace(unwrapped) ? normalized : unwrapped;
    }

    private static string ExtractLooseJsonStringValue(string block, string propertyName)
    {
        var match = Regex.Match(
            block,
            propertyName + @"\s*""?\s*:\s*""(?<value>(?:\\.|[^""])+)""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
            return string.Empty;

        return UnescapeLooseJsonString(match.Groups["value"].Value).Trim();
    }

    private static List<string> ExtractLooseStringArray(string block, string propertyName)
    {
        var arrayMatch = Regex.Match(
            block,
            propertyName + @"\s*""?\s*:\s*\[(?<value>.*?)\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!arrayMatch.Success)
            return [];

        return Regex.Matches(arrayMatch.Groups["value"].Value, @"""(?<item>(?:\\.|[^""])+)""", RegexOptions.Singleline)
            .Select(match => UnescapeLooseJsonString(match.Groups["item"].Value).Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string UnescapeLooseJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\\"", "\"")
            .Replace("\\/", "/")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\u0027", "'");
    }

    private static bool TryExtractJsonPayload(string content, out string json)
    {
        foreach (var candidate in EnumerateJsonCandidates(content))
        {
            if (!TryParseJsonCandidate(candidate, out json))
                continue;

            return true;
        }

        json = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string content)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(content);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(current))
                continue;

            var normalized = NormalizeJsonishText(current);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                continue;

            yield return normalized;

            var unwrappedString = TryUnwrapJsonString(normalized);
            if (!string.IsNullOrWhiteSpace(unwrappedString))
                queue.Enqueue(unwrappedString);

            var balancedJson = TryExtractBalancedJson(normalized);
            if (!string.IsNullOrWhiteSpace(balancedJson))
                queue.Enqueue(balancedJson);
        }
    }

    private static string NormalizeJsonishText(string content)
    {
        var normalized = content
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..].TrimStart(':', ' ', '\r', '\n', '\t');
        }

        if (normalized.StartsWith("`", StringComparison.Ordinal) && normalized.EndsWith("`", StringComparison.Ordinal))
        {
            normalized = normalized.Trim('`').Trim();
        }

        return normalized.Trim('\uFEFF', '\u200B', ' ', '\r', '\n', '\t');
    }

    private static bool TryParseJsonCandidate(string candidate, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var trimmed = candidate.Trim();
        if (!LooksLikeJson(trimmed))
            return false;

        try
        {
            using var _ = JsonDocument.Parse(trimmed);
            json = trimmed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryUnwrapJsonString(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[^1] != '"')
            return null;

        try
        {
            return JsonSerializer.Deserialize<string>(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractBalancedJson(string value)
    {
        var startIndex = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        char opening = '\0';
        char closing = '\0';

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (startIndex < 0)
            {
                if (ch == '{' || ch == '[')
                {
                    startIndex = i;
                    depth = 1;
                    opening = ch;
                    closing = ch == '{' ? '}' : ']';
                }

                continue;
            }

            if (ch == opening)
            {
                depth++;
            }
            else if (ch == closing)
            {
                depth--;
                if (depth == 0)
                    return value[startIndex..(i + 1)].Trim();
            }
        }

        return null;
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            || (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal));
    }

    private static string StripMarkdownCodeFence(string content)
    {
        return NormalizeJsonishText(content);
    }
    private static string TruncateForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 180 ? singleLine : $"{singleLine[..180]}...";
    }
}
