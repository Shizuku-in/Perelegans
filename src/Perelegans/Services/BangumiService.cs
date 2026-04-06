using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
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
        "ブランド",
        "品牌",
        "厂商",
        "社团",
        "开发",
        "开发商",
        "游戏开发商",
        "制作",
        "制作公司"
    ];
    private static readonly string[] FallbackBrandKeys =
    [
        "发行",
        "发行商",
        "publisher"
    ];

    public BangumiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MetadataResult>> SearchAsync(string query)
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

                if (!string.IsNullOrWhiteSpace(result.SourceId))
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
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var name))
            {
                var originalTitle = name.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(originalTitle))
                {
                    result.OriginalTitle = originalTitle;
                    result.Title = originalTitle;
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bangumi subject detail error: {ex.Message}");
        }
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
