using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using Perelegans.Models;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace Perelegans.Services;

/// <summary>
/// Fetches game metadata from ErogameScape via HTML scraping.
/// </summary>
public class ErogameSpaceService
{
    private readonly HttpClient _httpClient;
    private const string SearchUrl = "https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/kensaku.php";

    public ErogameSpaceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<MetadataResult>> SearchAsync(string query)
    {
        var results = new List<MetadataResult>();

        try
        {
            // POST search form
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("category", "game"),
                new KeyValuePair<string, string>("word_category", "name"),
                new KeyValuePair<string, string>("word", query),
                new KeyValuePair<string, string>("mode", "normal")
            });

            var response = await _httpClient.PostAsync(SearchUrl, formData);
            if (!response.IsSuccessStatusCode) return results;

            var html = await response.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Parse result table rows
            var rows = doc.DocumentNode.SelectNodes("//table[@id='query_result_table']//tr");
            if (rows == null) return results;

            // Skip header row
            for (int i = 1; i < Math.Min(rows.Count, 11); i++)
            {
                var cells = rows[i].SelectNodes("td");
                if (cells == null || cells.Count < 3) continue;

                var result = new MetadataResult
                {
                    Source = "ErogameSpace"
                };

                // Game link & title
                var linkNode = cells[0].SelectSingleNode(".//a");
                if (linkNode != null)
                {
                    result.Title = HttpUtility.HtmlDecode(linkNode.InnerText.Trim());
                    var href = linkNode.GetAttributeValue("href", "");
                    if (href.Contains("game="))
                    {
                        var gameId = href.Split("game=").Length > 1
                            ? href.Split("game=")[1].Split("&")[0]
                            : "";
                        result.SourceId = gameId;
                        result.WebUrl = $"https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game={gameId}";
                    }
                }

                // Brand (column 2)
                if (cells.Count > 1)
                {
                    result.Brand = HttpUtility.HtmlDecode(cells[1].InnerText.Trim());
                }

                // Release date (column 3)
                if (cells.Count > 2)
                {
                    var dateText = cells[2].InnerText.Trim();
                    if (DateTime.TryParse(dateText, out var dt))
                        result.ReleaseDate = dt;
                }

                results.Add(result);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ErogameSpace search error: {ex.Message}");
        }

        return results;
    }
}
