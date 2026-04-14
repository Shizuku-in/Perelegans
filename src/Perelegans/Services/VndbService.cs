using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Fetches visual novel metadata from VNDB API (v2 / Kana).
/// POST https://api.vndb.org/kana/vn
/// </summary>
public class VndbService
{
    private readonly HttpClient _httpClient;
    private const string ApiUrl = "https://api.vndb.org/kana/vn";

    public VndbService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MetadataResult>> SearchAsync(string query)
    {
        var results = new List<MetadataResult>();

        try
        {
            var requestBody = new
            {
                filters = new object[] { "search", "=", query },
                fields = "id, title, alttitle, titles.title, titles.lang, titles.main, released, developers.name, tags.name, image.url",
                results = 10
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ApiUrl, content);
            if (!response.IsSuccessStatusCode) return results;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("results", out var resultsArray))
                return results;

            foreach (var item in resultsArray.EnumerateArray())
                results.Add(ParseMetadataResult(item));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VNDB search error: {ex.Message}");
        }

        return results;
    }

    public async Task<MetadataResult?> GetByIdAsync(string vndbId)
    {
        var normalizedId = VndbIdUtilities.Normalize(vndbId);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return null;

        try
        {
            var requestBody = new
            {
                filters = new object[] { "id", "=", normalizedId },
                fields = "id, title, alttitle, titles.title, titles.lang, titles.main, released, developers.name, tags.name, image.url",
                results = 1
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ApiUrl, content);
            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("results", out var resultsArray) ||
                resultsArray.ValueKind != JsonValueKind.Array ||
                resultsArray.GetArrayLength() == 0)
            {
                return null;
            }

            return ParseMetadataResult(resultsArray[0]);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VNDB get-by-id error: {ex.Message}");
            return null;
        }
    }

    private static MetadataResult ParseMetadataResult(JsonElement item)
    {
        var result = new MetadataResult
        {
            Source = "VNDB",
            WebUrl = item.TryGetProperty("id", out var id)
                ? $"https://vndb.org/{id.GetString()}" : null,
            SourceId = item.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : ""
        };

        var fallbackTitle = item.TryGetProperty("title", out var title)
            ? title.GetString() ?? ""
            : "";
        result.OriginalTitle = item.TryGetProperty("alttitle", out var altTitle)
            ? altTitle.GetString() ?? ""
            : "";

        if (item.TryGetProperty("titles", out var titles))
        {
            foreach (var t in titles.EnumerateArray())
            {
                if (t.TryGetProperty("main", out var isMain) &&
                    isMain.ValueKind == JsonValueKind.True)
                {
                    result.OriginalTitle = t.TryGetProperty("title", out var ot)
                        ? ot.GetString() ?? ""
                        : "";
                    break;
                }
            }
        }

        result.Title = string.IsNullOrWhiteSpace(result.OriginalTitle)
            ? fallbackTitle
            : result.OriginalTitle;

        if (item.TryGetProperty("released", out var released))
        {
            var dateStr = released.GetString();
            if (TryParseFlexibleDate(dateStr, out var dt))
                result.ReleaseDate = dt;
        }

        if (item.TryGetProperty("developers", out var devs))
        {
            var devNames = new List<string>();
            foreach (var dev in devs.EnumerateArray())
            {
                if (dev.TryGetProperty("name", out var devName))
                    devNames.Add(devName.GetString() ?? "");
            }

            result.Brand = string.Join(", ", devNames);
        }

        if (item.TryGetProperty("tags", out var tags))
        {
            var tagNames = new List<string>();
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.TryGetProperty("name", out var tagName))
                {
                    var name = tagName.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        tagNames.Add(name);
                }
            }

            result.Tags = TagUtilities.Normalize(tagNames);
        }

        if (item.TryGetProperty("image", out var image) &&
            image.TryGetProperty("url", out var imageUrl))
        {
            result.ImageUrl = imageUrl.GetString();
        }

        return result;
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
}
