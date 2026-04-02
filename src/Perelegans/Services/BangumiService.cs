using System;
using System.Collections.Generic;
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

                if (item.TryGetProperty("name_cn", out var nameCn))
                {
                    var cn = nameCn.GetString() ?? "";
                    result.Title = !string.IsNullOrEmpty(cn) ? cn : result.OriginalTitle;
                }
                else
                {
                    result.Title = result.OriginalTitle;
                }

                if (item.TryGetProperty("air_date", out var airDate))
                {
                    var dateStr = airDate.GetString();
                    if (DateTime.TryParse(dateStr, out var dt))
                        result.ReleaseDate = dt;
                }

                if (item.TryGetProperty("images", out var images) &&
                    images.TryGetProperty("common", out var imgUrl))
                {
                    result.ImageUrl = imgUrl.GetString();
                }

                results.Add(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bangumi search error: {ex.Message}");
        }

        return results;
    }
}
