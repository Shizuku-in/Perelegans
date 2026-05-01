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
    private static readonly TimeSpan AiRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TagWeightRequestTimeout = TimeSpan.FromSeconds(60);
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
                    eligibleGames = profileSummary.EligibleLibraryGames,
                    topTags = profileSummary.TopPositiveTags.Take(5),
                    avoidTags = profileSummary.NegativeTags.Take(3),
                    preferredDevelopers = profileSummary.PreferredDevelopers.Take(3),
                    preferenceStyle = profileSummary.PreferenceStyle
                },
                candidates = candidates.Take(8).Select(candidate => new
                {
                    candidateId = candidate.VndbId,
                    title = candidate.DisplayTitle,
                    brand = candidate.Brand,
                    year = candidate.ReleaseDate?.Year,
                    matchingTags = candidate.MatchingTags.Take(4),
                    conflictingTags = candidate.ConflictingTags.Take(2),
                    localScore = Math.Round(candidate.RecommendationScore, 3),
                    rating = Math.Round(candidate.ExternalRatingScore ?? NormalizeRatingForPrompt(candidate.VndbRating) ?? 0, 3),
                    similar = candidate.SourceMatches.Select(match => match.Title).Take(2)
                })
            };

            using var request = BuildRequest(provider, baseUri, payload);
            using var timeoutCts = new System.Threading.CancellationTokenSource(AiRequestTimeout);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
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

                var textFallback = BuildTextFallbackExplanations(content, candidates);
                if (textFallback.Count > 0)
                {
                    result.Explanations = textFallback;
                    result.UserProfileSummary = string.Empty;
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

            var plainTextFallback = BuildTextFallbackExplanations(content, candidates);
            if (plainTextFallback.Count > 0)
            {
                result.Explanations = plainTextFallback;
                return result;
            }

            result.ErrorMessage = "AI JSON did not contain an 'explanations' array.";
            return result;
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"AI request timed out after {AiRequestTimeout.TotalSeconds:F0} seconds.";
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"AI parsing failed: {ex.Message}";
            return result;
        }
    }

    public async Task<Dictionary<string, CachedTagWeight>> ClassifyTagWeightsAsync(IEnumerable<string> tagNames)
    {
        var analysis = await ClassifyTagSemanticsAsync(tagNames);
        return analysis.TagWeights;
    }

    public async Task<AiTagSemanticAnalysisResult> ClassifyTagSemanticsAsync(IEnumerable<string> tagNames)
    {
        var tags = tagNames
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        if (!IsConfigured || tags.Count == 0)
            return new AiTagSemanticAnalysisResult();

        if (!Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return new AiTagSemanticAnalysisResult();

        try
        {
            var provider = ResolveProvider(baseUri);
            var payload = new { tags };
            var prompt =
                "Classify visual novel tags into semantic categories, merge equivalent concepts, and assign recommendation weights. " +
                "Return JSON only: {\"tags\":[{\"tagName\":\"...\",\"canonicalTag\":\"...\",\"aliases\":[\"...\"],\"category\":\"Genre|Theme|Character|Setting|Tone|ContentWarning|Structure|Technical|Meta\",\"weight\":1.0,\"confidence\":0.8}]}. " +
                "Weights should be 0.25-1.6. Genre, theme, tone, setting and content warnings are usually more important. Route structure, technical, metadata, protagonist/heroine implementation details are usually lower. " +
                $"Data: {JsonSerializer.Serialize(payload)}";

            var json = await SendJsonPromptAsync(
                provider,
                baseUri,
                prompt,
                "You classify visual novel tags for a deterministic recommender. Return JSON only.",
                maxTokens: 1100,
                TagWeightRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return new AiTagSemanticAnalysisResult();

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tags", out var tagArray) || tagArray.ValueKind != JsonValueKind.Array)
                return new AiTagSemanticAnalysisResult();

            var result = new AiTagSemanticAnalysisResult();
            foreach (var item in tagArray.EnumerateArray())
            {
                var tagName = item.TryGetProperty("tagName", out var tagNameElement)
                    ? tagNameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(tagName))
                    continue;

                var canonicalTag = item.TryGetProperty("canonicalTag", out var canonicalElement)
                    ? canonicalElement.GetString() ?? tagName
                    : tagName;
                var category = item.TryGetProperty("category", out var categoryElement)
                    ? categoryElement.GetString() ?? "Theme"
                    : "Theme";
                var weight = item.TryGetProperty("weight", out var weightElement) && weightElement.TryGetDouble(out var parsedWeight)
                    ? parsedWeight
                    : 1.0;
                var confidence = item.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var parsedConfidence)
                    ? parsedConfidence
                    : 0.7;

                var cachedWeight = new CachedTagWeight
                {
                    TagName = tagName.Trim(),
                    Category = category.Trim(),
                    Weight = Math.Clamp(weight, 0.25, 1.6),
                    Confidence = Math.Clamp(confidence, 0.35, 1.0),
                    Source = "ai",
                    CachedAtUtc = DateTimeOffset.UtcNow
                };
                result.TagWeights[RecommendationService.BuildTagWeightKey(tagName)] = cachedWeight;

                var aliases = item.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array
                    ? aliasArray.EnumerateArray()
                        .Where(alias => alias.ValueKind == JsonValueKind.String)
                        .Select(alias => alias.GetString())
                        .Where(alias => !string.IsNullOrWhiteSpace(alias))
                        .Select(alias => alias!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList()
                    : [];

                result.TagAliases[RecommendationService.BuildTagWeightKey(tagName)] = new CachedTagAlias
                {
                    TagName = tagName.Trim(),
                    CanonicalTag = string.IsNullOrWhiteSpace(canonicalTag) ? tagName.Trim() : canonicalTag.Trim(),
                    Aliases = aliases,
                    Category = category.Trim(),
                    Weight = Math.Clamp(weight, 0.25, 1.6),
                    Confidence = Math.Clamp(confidence, 0.35, 1.0),
                    CachedAtUtc = DateTimeOffset.UtcNow
                };
            }

            return result;
        }
        catch
        {
            return new AiTagSemanticAnalysisResult();
        }
    }

    public async Task<string> GenerateProfileSummaryAsync(TasteProfileSummary summary)
    {
        if (!IsConfigured || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return string.Empty;

        try
        {
            var provider = ResolveProvider(baseUri);
            var languageCode = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language);
            var outputLanguage = languageCode == "zh-Hans" ? "Simplified Chinese" : languageCode == "ja-JP" ? "Japanese" : "English";
            var prompt =
                $"Write one concise user taste profile summary in {outputLanguage}. Return JSON only: {{\"summary\":\"...\"}}. " +
                "Mention preferred themes/tags, avoided content, developers, and play pattern if present. Keep it under 60 words. " +
                $"Data: {JsonSerializer.Serialize(summary)}";
            var json = await SendJsonPromptAsync(provider, baseUri, prompt, "You summarize visual novel taste profiles. Return JSON only.", 260, AiRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("summary", out var element)
                ? element.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<List<string>> GenerateSearchQueriesAsync(string title, string? brand = null)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return [];

        try
        {
            var provider = ResolveProvider(baseUri);
            var payload = new { title, brand };
            var prompt =
                "Generate search queries for VNDB/Bangumi visual novel metadata lookup. Include likely Japanese title, romanization, English title, and common shortened title if inferable. " +
                "Return JSON only: {\"queries\":[\"...\"]}. Keep 3-8 unique queries, no explanations. " +
                $"Data: {JsonSerializer.Serialize(payload)}";
            var json = await SendJsonPromptAsync(provider, baseUri, prompt, "You generate metadata search aliases. Return JSON only.", 360, AiRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("queries", out var array) || array.ValueKind != JsonValueKind.Array)
                return [];

            return array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Where(item => !string.Equals(item, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<CachedMetadataConflict?> DetectMetadataConflictAsync(Game game, MetadataResult left, MetadataResult right)
    {
        if (!IsConfigured || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return null;

        try
        {
            var provider = ResolveProvider(baseUri);
            var payload = new
            {
                game = new { game.Title, game.Brand, game.ReleaseDate, game.VndbId, game.BangumiId },
                left,
                right
            };
            var prompt =
                "Decide whether two visual novel metadata records are likely mismatched. Return JSON only: {\"hasConflict\":false,\"confidence\":0.8,\"reason\":\"...\"}. " +
                "Use title similarity, developer/brand, release date, and source IDs. Keep reason short. " +
                $"Data: {JsonSerializer.Serialize(payload)}";
            var json = await SendJsonPromptAsync(provider, baseUri, prompt, "You detect metadata mismatches. Return JSON only.", 360, AiRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new CachedMetadataConflict
            {
                Key = $"{left.Source}:{left.SourceId}|{right.Source}:{right.SourceId}",
                HasConflict = root.TryGetProperty("hasConflict", out var hasConflict) && hasConflict.ValueKind == JsonValueKind.True,
                Confidence = root.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var parsedConfidence)
                    ? Math.Clamp(parsedConfidence, 0, 1)
                    : 0.5,
                Reason = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty,
                CachedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> GenerateCompletionNoteAsync(Game game)
    {
        if (!IsConfigured || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return string.Empty;

        try
        {
            var provider = ResolveProvider(baseUri);
            var languageCode = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language);
            var outputLanguage = languageCode == "zh-Hans" ? "Simplified Chinese" : languageCode == "ja-JP" ? "Japanese" : "English";
            var payload = new
            {
                game.Title,
                game.Brand,
                game.ReleaseDate,
                Status = game.Status.ToString(),
                PlaytimeHours = Math.Round(game.Playtime.TotalHours, 1),
                Tags = TagUtilities.Deserialize(game.Tags).Take(16)
            };
            var prompt =
                $"Draft a short editable personal completion note in {outputLanguage}. Return JSON only: {{\"note\":\"...\"}}. " +
                "Do not pretend to know plot details beyond provided tags. Keep it under 80 words. " +
                $"Data: {JsonSerializer.Serialize(payload)}";
            var json = await SendJsonPromptAsync(provider, baseUri, prompt, "You draft visual novel library notes. Return JSON only.", 320, AiRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("note", out var note)
                ? note.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<AiLibraryInsightsResult> AnalyzeLibraryAsync(IReadOnlyCollection<Game> games)
    {
        var result = new AiLibraryInsightsResult();
        if (!IsConfigured || games.Count == 0 || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return result;

        try
        {
            var provider = ResolveProvider(baseUri);
            var languageCode = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language);
            var outputLanguage = languageCode == "zh-Hans" ? "Simplified Chinese" : languageCode == "ja-JP" ? "Japanese" : "English";
            var payload = games
                .OrderByDescending(game => game.AccessedDate)
                .Take(80)
                .Select(game => new
                {
                    game.Id,
                    game.Title,
                    game.Brand,
                    game.ReleaseDate,
                    Status = game.Status.ToString(),
                    PlaytimeHours = Math.Round(game.Playtime.TotalHours, 1),
                    game.VndbId,
                    game.BangumiId,
                    game.ErogameSpaceId,
                    HasCover = !string.IsNullOrWhiteSpace(game.CoverDisplaySource),
                    Tags = TagUtilities.Deserialize(game.Tags).Take(24)
                });

            var prompt =
                $"Analyze this local visual novel library in {outputLanguage}. Return JSON only with this shape: " +
                "{\"report\":\"...\",\"dataIssues\":[\"...\"],\"normalizationSuggestions\":[\"...\"],\"metadataMergeSuggestions\":[\"...\"],\"tagCleanupSuggestions\":[\"...\"],\"gameSummaries\":[{\"gameId\":1,\"summary\":\"...\"}],\"titleAliases\":[{\"gameId\":1,\"queries\":[\"...\"]}]}. " +
                "Cover metadata merge conflicts, title aliases, tag cleanup/translation/merging, short game summaries, play habit report, data health issues, and naming normalization. " +
                "Do not recommend new games. Keep each list item short and actionable. " +
                $"Data: {JsonSerializer.Serialize(payload)}";
            var json = await SendJsonPromptAsync(
                provider,
                baseUri,
                prompt,
                "You are a local visual novel library data assistant. Return JSON only.",
                maxTokens: 1800,
                AiRequestTimeout);
            if (string.IsNullOrWhiteSpace(json))
                return result;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            result.Report = root.TryGetProperty("report", out var report) ? report.GetString() ?? string.Empty : string.Empty;
            result.DataIssues = ReadStringList(root, "dataIssues", 12);
            result.NormalizationSuggestions = ReadStringList(root, "normalizationSuggestions", 12);
            result.MetadataMergeSuggestions = ReadStringList(root, "metadataMergeSuggestions", 12);
            result.TagCleanupSuggestions = ReadStringList(root, "tagCleanupSuggestions", 12);

            if (root.TryGetProperty("gameSummaries", out var summaries) && summaries.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in summaries.EnumerateArray())
                {
                    if (!item.TryGetProperty("gameId", out var idElement) || !idElement.TryGetInt32(out var gameId))
                        continue;
                    var summary = item.TryGetProperty("summary", out var summaryElement)
                        ? summaryElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(summary))
                        result.GameSummaries[gameId] = summary.Trim();
                }
            }

            if (root.TryGetProperty("titleAliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in aliases.EnumerateArray())
                {
                    if (!item.TryGetProperty("gameId", out var idElement) || !idElement.TryGetInt32(out var gameId))
                        continue;
                    if (!item.TryGetProperty("queries", out var queriesElement) || queriesElement.ValueKind != JsonValueKind.Array)
                        continue;

                    var queries = queriesElement.EnumerateArray()
                        .Where(query => query.ValueKind == JsonValueKind.String)
                        .Select(query => query.GetString())
                        .Where(query => !string.IsNullOrWhiteSpace(query))
                        .Select(query => query!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList();
                    if (queries.Count > 0)
                        result.TitleAliases[gameId] = queries;
                }
            }

            return result;
        }
        catch
        {
            return result;
        }
    }

    public async Task<string> AnswerLibraryQuestionAsync(
        string question,
        IReadOnlyCollection<Game> relevantGames,
        object libraryStats,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(question) || !Uri.TryCreate(_settingsService.Settings.AiApiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
            return string.Empty;

        try
        {
            var provider = ResolveProvider(baseUri);
            var languageCode = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language);
            var outputLanguage = languageCode == "zh-Hans" ? "Simplified Chinese" : languageCode == "ja-JP" ? "Japanese" : "English";
            var payload = new
            {
                question,
                libraryStats,
                games = relevantGames.Select(game => new
                {
                    game.Id,
                    game.Title,
                    game.Brand,
                    game.ReleaseDate,
                    Status = game.Status.ToString(),
                    PlaytimeHours = Math.Round(game.Playtime.TotalHours, 1),
                    LastPlayed = game.AccessedDate,
                    game.VndbId,
                    game.BangumiId,
                    HasCover = !string.IsNullOrWhiteSpace(game.CoverDisplaySource),
                    Tags = TagUtilities.Deserialize(game.Tags).Take(18)
                })
            };
            var prompt =
                $"Answer the user's question about their local visual novel library in {outputLanguage}. " +
                "Use only the provided library data. If the data is insufficient, say what is missing. " +
                "Return JSON only: {\"answer\":\"...\"}. Keep the answer concise and actionable. " +
                $"Data: {JsonSerializer.Serialize(payload)}";
            var json = await SendJsonPromptAsync(
                provider,
                baseUri,
                prompt,
                "You are a local library assistant. Do not invent facts beyond the supplied data. Return JSON only.",
                maxTokens: 700,
                AiRequestTimeout,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("answer", out var answerElement)
                ? answerElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> ReadStringList(JsonElement root, string propertyName, int maxCount)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }

    private async Task<string> SendJsonPromptAsync(
        AiProvider provider,
        Uri baseUri,
        string prompt,
        string systemPrompt,
        int maxTokens,
        TimeSpan timeout,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var request = BuildJsonRequest(provider, baseUri, prompt, systemPrompt, maxTokens);
        using var timeoutCts = new System.Threading.CancellationTokenSource(timeout);
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        using var response = await _httpClient.SendAsync(request, linkedCts.Token);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        var responseBody = await response.Content.ReadAsStringAsync();
        var content = ExtractAssistantContent(responseBody, provider);
        if (string.IsNullOrWhiteSpace(content) || !TryExtractJsonPayload(content, out var json))
            return string.Empty;

        return json;
    }

    private HttpRequestMessage BuildJsonRequest(
        AiProvider provider,
        Uri baseUri,
        string userPrompt,
        string systemPrompt,
        int maxTokens)
    {
        var model = _settingsService.Settings.AiModel.Trim();
        var apiKey = _settingsService.Settings.AiApiKey.Trim();
        if (provider == AiProvider.Anthropic)
        {
            var anthropicBody = new
            {
                model,
                max_tokens = maxTokens,
                temperature = 0.2,
                system = systemPrompt,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt
                    }
                }
            };

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
            temperature = 0.2,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
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

    private HttpRequestMessage BuildRequest(AiProvider provider, Uri baseUri, object payload)
    {
        var languageCode = TranslationService.NormalizeLanguageCode(_settingsService.Settings.Language);
        var outputLanguage = languageCode switch
        {
            "zh-Hans" => "Simplified Chinese",
            "ja-JP" => "Japanese",
            _ => "English"
        };
        var userPrompt =
            "Return JSON only. Do not explain the task, do not include Markdown, and do not wrap the JSON in quotes. " +
            "The exact shape is {\"userProfileSummary\":\"...\",\"explanations\":[{\"candidateId\":\"v1\",\"reason\":\"...\",\"matchingTags\":[\"...\"],\"caution\":\"\",\"sellingPoint\":\"...\",\"affinityScore\":0.8}]}. " +
            $"All user-facing text values must be written in {outputLanguage}. " +
            "Do not use English for reason, caution, sellingPoint, or userProfileSummary unless the requested language is English. " +
            "Return one item per candidate. Each item must contain candidateId, reason, matchingTags, caution, and sellingPoint. " +
            "Each item may include affinityScore from 0.0 to 1.0, where 1.0 means very aligned with the generated user taste profile. Use the full range and make the scores discriminative, not clustered. " +
            "matchingTags must be an array of short strings already present in the candidate data. " +
            "reason must be one short sentence under 35 words. caution can be empty. " +
            "When Bangumi data exists, consider bangumiRating, bangumiRank, and externalRatingScore; externalRatingScore weighs Bangumi and VNDB equally when both are available. " +
            "sellingPoint should be a short label like 'Tag match', 'Developer pick', 'Recent-taste fit', or 'Contrast pick'. " +
            $"Data: {JsonSerializer.Serialize(payload)}";
        return BuildJsonRequest(
            provider,
            baseUri,
            userPrompt,
            $"You explain visual novel recommendations. Return JSON only. Keep each reason concise and practical. Write all user-facing text in {outputLanguage}.",
            maxTokens: 500);
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

        if (provider == AiProvider.Anthropic)
        {
            return normalizedBase.AbsolutePath.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
                ? normalizedBase
                : new Uri(normalizedBase, "v1/messages");
        }

        return normalizedBase.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalizedBase
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

    private static double? NormalizeRatingForPrompt(double? rating)
    {
        if (!rating.HasValue || rating.Value <= 0)
            return null;

        var value = rating.Value > 10 ? rating.Value / 100d : rating.Value / 10d;
        return Math.Clamp(value, 0, 1);
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

    private static List<AiRecommendationExplanation> BuildTextFallbackExplanations(
        string content,
        IReadOnlyList<RecommendationCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(content) || candidates.Count == 0)
            return [];

        var normalized = NormalizeLooseExtractionText(content);
        if (string.IsNullOrWhiteSpace(normalized) || LooksLikeInstructionEcho(normalized))
            return [];

        var orderedReasons = ExtractFallbackReasonLines(normalized)
            .Select(TrimFallbackReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason) && !LooksLikeInstructionEcho(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(candidates.Count)
            .ToList();

        if (orderedReasons.Count == 0)
        {
            var singleReason = TrimFallbackReason(normalized);
            if (string.IsNullOrWhiteSpace(singleReason) || LooksLikeInstructionEcho(singleReason))
                return [];

            orderedReasons.Add(singleReason);
        }

        var explanations = new List<AiRecommendationExplanation>();
        for (var i = 0; i < orderedReasons.Count && i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var candidateId = VndbIdUtilities.Normalize(candidate.VndbId);
            if (string.IsNullOrWhiteSpace(candidateId))
                continue;

            explanations.Add(new AiRecommendationExplanation
            {
                CandidateId = candidateId,
                Reason = orderedReasons[i],
                MatchingTags = candidate.MatchingTags.Take(3).ToList(),
                SellingPoint = "AI note"
            });
        }

        return explanations;
    }

    private static IEnumerable<string> ExtractFallbackReasonLines(string content)
    {
        var normalized = Regex.Replace(content, @"\r\n?|\u2028|\u2029", "\n");
        normalized = Regex.Replace(normalized, @"(?m)^\s*(?:[-*\u2022]|\d+[\.)]|[\uFF08(]?\d+[\uFF09)])\s*", string.Empty);

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 8)
                continue;

            yield return trimmed;
        }
    }

    private static string TrimFallbackReason(string value)
    {
        var cleaned = StripMarkdownCodeFence(value);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        cleaned = cleaned.Trim('"', '\'', '`', '-', '*', '\u2022', '\uFF1A', ':', ' ', '\t');

        const int maxLength = 140;
        if (cleaned.Length <= maxLength)
            return cleaned;

        var sentenceEnd = cleaned.IndexOfAny(['。', '！', '？', '.', '!', '?']);
        if (sentenceEnd > 20 && sentenceEnd < maxLength)
            return cleaned[..(sentenceEnd + 1)].Trim();

        return cleaned[..maxLength].TrimEnd() + "...";
    }

    private static bool LooksLikeInstructionEcho(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var lower = value.ToLowerInvariant();
        var suspiciousHits = 0;
        string[] suspiciousTerms =
        [
            "return json",
            "json object",
            "explanations array",
            "candidateid",
            "matchingtags",
            "userprofilesummary",
            "reply exactly",
            "we need to",
            "the response should",
            "no extra text"
        ];

        foreach (var term in suspiciousTerms)
        {
            if (lower.Contains(term, StringComparison.Ordinal))
                suspiciousHits++;
        }

        return suspiciousHits >= 2;
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

public sealed class AiTagSemanticAnalysisResult
{
    public Dictionary<string, CachedTagWeight> TagWeights { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CachedTagAlias> TagAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AiLibraryInsightsResult
{
    public string Report { get; set; } = string.Empty;
    public List<string> DataIssues { get; set; } = [];
    public List<string> NormalizationSuggestions { get; set; } = [];
    public List<string> MetadataMergeSuggestions { get; set; } = [];
    public List<string> TagCleanupSuggestions { get; set; } = [];
    public Dictionary<int, string> GameSummaries { get; } = new();
    public Dictionary<int, List<string>> TitleAliases { get; } = new();

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Report) ||
        DataIssues.Count > 0 ||
        NormalizationSuggestions.Count > 0 ||
        MetadataMergeSuggestions.Count > 0 ||
        TagCleanupSuggestions.Count > 0 ||
        GameSummaries.Count > 0 ||
        TitleAliases.Count > 0;
}
