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
                fields = "id, title, alttitle, titles.title, titles.lang, titles.main, released, developers.name, tags.name",
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

                // Prefer the original-language title instead of the site display title/romanization.
                if (item.TryGetProperty("titles", out var titles))
                {
                    foreach (var t in titles.EnumerateArray())
                    {
                        if (t.TryGetProperty("main", out var isMain) &&
                            isMain.ValueKind == JsonValueKind.True)
                        {
                            result.OriginalTitle = t.TryGetProperty("title", out var ot)
                                ? ot.GetString() ?? "" : "";
                            break;
                        }
                    }
                }

                result.Title = string.IsNullOrWhiteSpace(result.OriginalTitle)
                    ? fallbackTitle
                    : result.OriginalTitle;

                // Parse release date
                if (item.TryGetProperty("released", out var released))
                {
                    var dateStr = released.GetString();
                    if (TryParseFlexibleDate(dateStr, out var dt))
                        result.ReleaseDate = dt;
                }

                // Parse developers
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

                results.Add(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VNDB search error: {ex.Message}");
        }

        return results;
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
