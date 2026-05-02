using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Fetches game metadata from Bangumi API.
/// GET https://api.bgm.tv/search/subject/{keyword}?type=4
/// </summary>
public class BangumiService
{
    private readonly HttpClient _httpClient;
    private const string ApiBase = "https://api.bgm.tv";
    private static readonly string[] PreferredBrandKeys =
    [
        "\u30d6\u30e9\u30f3\u30c9",
        "\u54c1\u724c",
        "\u5382\u5546",
        "\u793e\u56e2",
        "\u5f00\u53d1",
        "\u5f00\u53d1\u5546",
        "\u6e38\u620f\u5f00\u53d1\u5546",
        "\u5236\u4f5c",
        "\u5236\u4f5c\u516c\u53f8"
    ];
    private static readonly string[] FallbackBrandKeys =
    [
        "\u53d1\u884c",
        "\u53d1\u884c\u5546",
        "publisher"
    ];

    public BangumiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<List<MetadataResult>> SearchAsync(string query)
    {
        return SearchAsync(query, includeDetails: true);
    }

    public async Task<List<MetadataResult>> SearchAsync(string query, bool includeDetails)
    {
        var results = new List<MetadataResult>();

        try
        {
            var url = $"{ApiBase}/search/subject/{Uri.EscapeDataString(query)}?type=4&responseGroup=small&max_results=10";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Perelegans/0.2");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return results;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("list", out var list))
                return results;

            foreach (var item in list.EnumerateArray())
            {
                var result = new MetadataResult
                {
                    Source = "Bangumi"
                };

                if (item.TryGetProperty("id", out var id))
                {
                    result.SourceId = id.GetInt32().ToString();
                    result.WebUrl = $"https://bgm.tv/subject/{result.SourceId}";
                }

                if (item.TryGetProperty("name", out var name))
                    result.OriginalTitle = name.GetString() ?? "";

                if (item.TryGetProperty("name_cn", out var nameCn))
                    result.ChineseTitle = nameCn.GetString() ?? "";

                result.Title = result.ChineseTitle;
                if (string.IsNullOrWhiteSpace(result.Title))
                    result.Title = result.OriginalTitle;

                if (item.TryGetProperty("date", out var date) &&
                    TryParseFlexibleDate(date.GetString(), out var parsedDate))
                {
                    result.ReleaseDate = parsedDate;
                }
                else if (item.TryGetProperty("air_date", out var airDate) &&
                         TryParseFlexibleDate(airDate.GetString(), out parsedDate))
                {
                    result.ReleaseDate = parsedDate;
                }

                if (item.TryGetProperty("images", out var images) &&
                    images.TryGetProperty("common", out var imgUrl))
                {
                    result.ImageUrl = imgUrl.GetString();
                }

                ApplyRating(item, result);

                if (includeDetails && !string.IsNullOrWhiteSpace(result.SourceId))
                    await PopulateSubjectDetailsAsync(result);

                results.Add(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bangumi search error: {ex.Message}");
        }

        return results;
    }

    public async Task<MetadataResult?> GetByIdAsync(string bangumiId)
    {
        if (string.IsNullOrWhiteSpace(bangumiId))
            return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v0/subjects/{bangumiId.Trim()}");
            request.Headers.Add("User-Agent", "Perelegans/0.2");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            return ParseSubjectResult(doc.RootElement, bangumiId.Trim());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bangumi get-by-id error: {ex.Message}");
            return null;
        }
    }

    public async Task<BangumiAccount?> GetCurrentAccountAsync(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var request = BuildAuthenticatedRequest(HttpMethod.Get, $"{ApiBase}/v0/me", accessToken);
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        return new BangumiAccount
        {
            Id = TryReadInt64(root, "id") ?? 0,
            Username = TryReadString(root, "username") ?? string.Empty,
            Nickname = TryReadString(root, "nickname") ?? string.Empty
        };
    }

    public async Task<BangumiCollectionState?> GetCollectionAsync(string bangumiId, string accessToken)
    {
        if (!int.TryParse(bangumiId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var subjectId) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        using var request = BuildAuthenticatedRequest(HttpMethod.Get, $"{ApiBase}/v0/users/-/collections/{subjectId}", accessToken);
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        return ParseCollectionState(doc.RootElement, subjectId);
    }

    public async Task<List<BangumiCollectionState>> GetGameCollectionsAsync(string accessToken)
    {
        var results = new List<BangumiCollectionState>();
        if (string.IsNullOrWhiteSpace(accessToken))
            return results;

        var account = await GetCurrentAccountAsync(accessToken);
        var userKeys = new List<string> { "-" };
        if (!string.IsNullOrWhiteSpace(account?.Username))
            userKeys.Add(account.Username);
        if (account?.Id > 0)
            userKeys.Add(account.Id.ToString(CultureInfo.InvariantCulture));

        foreach (var userKey in userKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var query in new[] { "subject_type=4", "type=4", string.Empty })
            {
                results = await GetCollectionsPageByPageAsync(userKey, query, accessToken);
                if (results.Count > 0)
                    return results;
            }
        }

        return results;
    }

    private async Task<List<BangumiCollectionState>> GetCollectionsPageByPageAsync(
        string userKey,
        string query,
        string accessToken)
    {
        var results = new List<BangumiCollectionState>();
        const int limit = 50;
        var offset = 0;
        var encodedUser = Uri.EscapeDataString(userKey);

        while (true)
        {
            var paging = string.IsNullOrWhiteSpace(query)
                ? $"limit={limit}&offset={offset}"
                : $"{query}&limit={limit}&offset={offset}";

            using var request = BuildAuthenticatedRequest(
                HttpMethod.Get,
                $"{ApiBase}/v0/users/{encodedUser}/collections?{paging}",
                accessToken);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return results;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            var pageCount = 0;
            foreach (var item in data.EnumerateArray())
            {
                var collection = ParseCollectionState(item, fallbackSubjectId: 0);
                if (collection is { SubjectId: > 0 })
                    results.Add(collection);
                pageCount++;
            }

            offset += pageCount;
            var total = TryReadInt32(doc.RootElement, "total");
            if (pageCount == 0 || pageCount < limit || (total.HasValue && offset >= total.Value))
                return results;
        }
    }

    public async Task<bool> UpdateCollectionAsync(
        string bangumiId,
        string accessToken,
        GameStatus status,
        int? rating,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var (success, _, _) = await UpdateCollectionAsyncWithDebug(bangumiId, accessToken, status, rating, comment, cancellationToken);
        return success;
    }

    public async Task<(bool success, string requestInfo, string responseInfo)> UpdateCollectionAsyncWithDebug(
        string bangumiId,
        string accessToken,
        GameStatus status,
        int? rating,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(bangumiId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var subjectId) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            var info = $"参数无效 - bangumiId={bangumiId}, accessToken={(string.IsNullOrWhiteSpace(accessToken) ? "empty" : "present")}";
            return (false, info, "N/A");
        }

        var collectionType = MapGameStatusToCollectionType(status);
        var payload = new Dictionary<string, object?>
        {
            ["type"] = collectionType,
            ["rate"] = rating is >= 1 and <= 10 ? rating.Value : 0,
            ["comment"] = comment?.Trim() ?? string.Empty
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var url = $"{ApiBase}/v0/users/-/collections/{subjectId}";
        var requestInfo = $"PATCH {url}\nPayload: {payloadJson}\nToken: {accessToken.Substring(0, Math.Min(20, accessToken.Length))}...";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = BuildAuthenticatedRequest(HttpMethod.Patch, url, accessToken);
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        try
        {
            using var response = await _httpClient.SendAsync(request, cts.Token);
            var responseContent = await response.Content.ReadAsStringAsync(cts.Token);
            var responseInfo = $"Status: {(int)response.StatusCode} {response.ReasonPhrase}\nBody: {responseContent}";

            // Fallback: If subject is not collected, create it via old API
            if ((int)response.StatusCode == 404 && responseContent.Contains("subject not collected", StringComparison.OrdinalIgnoreCase))
            {
                requestInfo += "\n[Fallback] Subject not collected, retrying with old API to create collection...";
                
                // Old Bangumi API: POST /collection/{subject_id}/add?type={type}
                var oldApiUrl = $"{ApiBase}/collection/{subjectId}/add?type={collectionType}";
                
                // Add rate and comment as form data if present
                var formContent = new List<KeyValuePair<string, string>>();
                if (rating is >= 1 and <= 10)
                    formContent.Add(new KeyValuePair<string, string>("rate", rating.Value.ToString()));
                if (!string.IsNullOrWhiteSpace(comment))
                    formContent.Add(new KeyValuePair<string, string>("comment", comment.Trim()));

                using var postRequest = BuildAuthenticatedRequest(HttpMethod.Post, oldApiUrl, accessToken);
                if (formContent.Count > 0)
                {
                    postRequest.Content = new FormUrlEncodedContent(formContent);
                }

                using var postResponse = await _httpClient.SendAsync(postRequest, cts.Token);
                var postResponseContent = await postResponse.Content.ReadAsStringAsync(cts.Token);
                responseInfo = $"[POST Old API] Status: {(int)postResponse.StatusCode} {postResponse.ReasonPhrase}\nBody: {postResponseContent}";
                return (postResponse.IsSuccessStatusCode, requestInfo, responseInfo);
            }

            return (response.IsSuccessStatusCode, requestInfo, responseInfo);
        }
        catch (OperationCanceledException)
        {
            return (false, requestInfo, "请求超时 (15秒)");
        }
        catch (Exception ex)
        {
            return (false, requestInfo, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task PopulateSubjectDetailsAsync(MetadataResult result)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v0/subjects/{result.SourceId}");
            request.Headers.Add("User-Agent", "Perelegans/0.2");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            ApplySubjectDetails(doc.RootElement, result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bangumi subject detail error: {ex.Message}");
        }
    }

    public static GameStatus MapCollectionTypeToGameStatus(int collectionType)
    {
        return collectionType switch
        {
            1 => GameStatus.Planned,
            2 => GameStatus.Completed,
            3 => GameStatus.Playing,
            5 => GameStatus.Dropped,
            _ => GameStatus.Planned
        };
    }

    public static int MapGameStatusToCollectionType(GameStatus status)
    {
        return status switch
        {
            GameStatus.Planned => 1,
            GameStatus.Completed => 2,
            GameStatus.Playing => 3,
            GameStatus.Dropped => 5,
            _ => 1
        };
    }

    private static HttpRequestMessage BuildAuthenticatedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("User-Agent", "Perelegans/0.2 (https://github.com/Shizuku-in/Perelegans)");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        return request;
    }

    private static BangumiCollectionState? ParseCollectionState(JsonElement root, int fallbackSubjectId)
    {
        var type = TryReadInt32(root, "type") ??
                   TryReadNestedInt32(root, "type", "id") ??
                   TryReadCollectionTypeString(root, "type") ??
                   TryReadCollectionTypeString(root, "status") ??
                   TryReadCollectionTypeString(root, "collection_status") ??
                   2;

        return new BangumiCollectionState
        {
            SubjectId = TryReadInt32(root, "subject_id") ??
                        TryReadNestedInt32(root, "subject", "id") ??
                        fallbackSubjectId,
            Type = type,
            Rating = TryReadInt32(root, "rate") ?? TryReadInt32(root, "rating"),
            Comment = TryReadString(root, "comment"),
            UpdatedAt = TryReadDateTime(root, "updated_at") ?? TryReadDateTime(root, "updatedAt")
        };
    }

    private static MetadataResult ParseSubjectResult(JsonElement root, string sourceId)
    {
        var result = new MetadataResult
        {
            Source = "Bangumi",
            SourceId = sourceId,
            WebUrl = $"https://bgm.tv/subject/{sourceId}"
        };

        if (root.TryGetProperty("images", out var images) &&
            images.TryGetProperty("common", out var imgUrl))
        {
            result.ImageUrl = imgUrl.GetString();
        }

        ApplyRating(root, result);
        ApplySubjectDetails(root, result);
        return result;
    }

    private static void ApplySubjectDetails(JsonElement root, MetadataResult result)
    {
        if (root.TryGetProperty("name", out var name))
        {
            var originalTitle = name.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(originalTitle))
            {
                result.OriginalTitle = originalTitle;
                result.Title = originalTitle;
            }
        }

        if (root.TryGetProperty("name_cn", out var nameCn))
        {
            var chineseTitle = nameCn.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(chineseTitle))
            {
                result.ChineseTitle = chineseTitle;
                result.Title = chineseTitle;
            }
        }

        if (root.TryGetProperty("date", out var date) &&
            TryParseFlexibleDate(date.GetString(), out var parsedDate))
        {
            result.ReleaseDate = parsedDate;
        }

        if (root.TryGetProperty("infobox", out var infobox))
        {
            var brand = ExtractBrand(infobox);
            if (!string.IsNullOrWhiteSpace(brand))
                result.Brand = brand;
        }

        var tagNames = new List<string>();

        if (root.TryGetProperty("tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.TryGetProperty("name", out var tagName))
                {
                    var tagValue = tagName.GetString();
                    if (!string.IsNullOrWhiteSpace(tagValue))
                        tagNames.Add(tagValue);
                }
            }
        }

        result.Tags = TagUtilities.Normalize(tagNames);
    }

    private static void ApplyRating(JsonElement root, MetadataResult result)
    {
        if (!root.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object)
            return;

        if (rating.TryGetProperty("score", out var scoreElement) &&
            TryReadDouble(scoreElement, out var score))
        {
            result.Rating = score;
        }

        if (rating.TryGetProperty("rank", out var rankElement) &&
            TryReadInt32(rankElement, out var rank))
        {
            result.Rank = rank;
        }

        if (rating.TryGetProperty("total", out var totalElement) &&
            TryReadInt32(totalElement, out var total))
        {
            result.VoteCount = total;
        }
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return true;

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static int? TryReadInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && TryReadInt32(element, out var value)
            ? value
            : null;
    }

    private static long? TryReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
            return value;

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static int? TryReadNestedInt32(JsonElement root, string propertyName, string nestedPropertyName)
    {
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Object
            ? TryReadInt32(element, nestedPropertyName)
            : null;
    }

    private static string? TryReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static int? TryReadCollectionTypeString(JsonElement root, string propertyName)
    {
        var value = TryReadString(root, propertyName);
        return value?.Trim().ToLowerInvariant() switch
        {
            "wish" => 1,
            "collect" => 2,
            "doing" => 3,
            "on_hold" => 4,
            "dropped" => 5,
            _ => null
        };
    }

    private static DateTime? TryReadDateTime(JsonElement root, string propertyName)
    {
        var value = TryReadString(root, propertyName);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
    }

    private static bool TryParseFlexibleDate(string? value, out DateTime date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = default;
            return false;
        }

        return DateTime.TryParseExact(
                   value,
                   ["yyyy-MM-dd", "yyyy-MM", "yyyy"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out date)
               || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string ExtractBrand(JsonElement infobox)
    {
        var preferred = ExtractInfoboxValue(infobox, PreferredBrandKeys);
        return !string.IsNullOrWhiteSpace(preferred)
            ? preferred
            : ExtractInfoboxValue(infobox, FallbackBrandKeys);
    }

    private static string ExtractInfoboxValue(JsonElement infobox, IReadOnlyCollection<string> candidateKeys)
    {
        if (infobox.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var entry in infobox.EnumerateArray())
        {
            if (!entry.TryGetProperty("key", out var keyElement))
                continue;

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            foreach (var candidateKey in candidateKeys)
            {
                if (!key.Contains(candidateKey, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.TryGetProperty("value", out var valueElement))
                    return FlattenInfoboxValue(valueElement);
            }
        }

        return string.Empty;
    }

    private static string FlattenInfoboxValue(JsonElement value)
    {
        var values = new List<string>();
        CollectInfoboxValues(value, values);

        return string.Join(", ", values);
    }

    private static void CollectInfoboxValues(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddInfoboxValue(values, element.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectInfoboxValues(item, values);
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("v", out var v))
                {
                    CollectInfoboxValues(v, values);
                }
                else if (element.TryGetProperty("value", out var nestedValue))
                {
                    CollectInfoboxValues(nestedValue, values);
                }
                else if (element.TryGetProperty("k", out var k))
                {
                    CollectInfoboxValues(k, values);
                }
                else if (element.TryGetProperty("name", out var name))
                {
                    CollectInfoboxValues(name, values);
                }
                break;
        }
    }

    private static void AddInfoboxValue(ICollection<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || values.Contains(trimmed))
            return;

        values.Add(trimmed);
    }
}
