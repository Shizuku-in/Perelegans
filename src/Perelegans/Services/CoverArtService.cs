using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Perelegans.Models;

namespace Perelegans.Services;

public class CoverArtService
{
    private readonly HttpClient _httpClient;
    private static readonly string CoverCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perelegans",
        "covers");

    public CoverArtService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string?> ResolveCoverUrlAsync(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.BangumiId))
        {
            var bangumiCover = await TryResolveBangumiCoverAsync(game.BangumiId);
            if (!string.IsNullOrWhiteSpace(bangumiCover))
                return bangumiCover;
        }

        var bangumiSearchCover = await TryResolveBangumiCoverBySearchAsync(game.Title);
        if (!string.IsNullOrWhiteSpace(bangumiSearchCover))
            return bangumiSearchCover;

        if (!string.IsNullOrWhiteSpace(game.VndbId))
        {
            var vndbCover = await TryResolveVndbCoverAsync(game.VndbId);
            if (!string.IsNullOrWhiteSpace(vndbCover))
                return vndbCover;
        }

        var vndbSearchCover = await TryResolveVndbCoverBySearchAsync(game.Title);
        if (!string.IsNullOrWhiteSpace(vndbSearchCover))
            return vndbSearchCover;

        return null;
    }

    private async Task<string?> TryResolveBangumiCoverBySearchAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            var bangumiService = new BangumiService(_httpClient);
            var results = await bangumiService.SearchAsync(title.Trim());
            return results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ImageUrl))?.ImageUrl;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> TryResolveBangumiCoverAsync(string bangumiId)
    {
        if (string.IsNullOrWhiteSpace(bangumiId))
            return null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{bangumiId.Trim()}");
            request.Headers.Add("User-Agent", "Perelegans/0.2");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("images", out var images) &&
                images.TryGetProperty("common", out var commonImage))
            {
                return commonImage.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    public async Task<string?> TryResolveVndbCoverAsync(string vndbId)
    {
        var normalizedId = VndbIdUtilities.Normalize(vndbId);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return null;

        try
        {
            var payload = new
            {
                filters = new object[] { "id", "=", normalizedId },
                fields = "image.url",
                results = 1
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn");
            request.Headers.Add("User-Agent", "Perelegans/0.2");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return null;
            }

            var firstResult = results[0];
            if (firstResult.TryGetProperty("image", out var image) &&
                image.TryGetProperty("url", out var imageUrl))
            {
                return imageUrl.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<string?> TryResolveVndbCoverBySearchAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            var vndbService = new VndbService(_httpClient);
            var results = await vndbService.SearchAsync(title.Trim());
            return results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ImageUrl))?.ImageUrl;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> ResolveAndCacheCoverAsync(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.CoverImagePath) && File.Exists(game.CoverImagePath))
        {
            UpdateCoverMetadataFromFile(game, game.CoverImagePath);
            return game.CoverImagePath;
        }

        var coverUrl = await ResolveCoverUrlAsync(game);
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        var cachedPath = await DownloadCoverAsync(game.Id, coverUrl);
        if (!string.IsNullOrWhiteSpace(cachedPath))
        {
            game.CoverImageUrl = coverUrl;
            game.CoverImagePath = cachedPath;
            UpdateCoverMetadataFromFile(game, cachedPath);
            return cachedPath;
        }

        game.CoverImageUrl = coverUrl;
        return coverUrl;
    }

    private async Task<string?> DownloadCoverAsync(int gameId, string coverUrl)
    {
        try
        {
            Directory.CreateDirectory(CoverCacheDir);

            var extension = GetExtensionFromUrl(coverUrl);
            var filePath = Path.Combine(CoverCacheDir, $"game-{gameId}{extension}");

            using var response = await _httpClient.GetAsync(coverUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var imageStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(filePath);
            await imageStream.CopyToAsync(fileStream);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    private static string GetExtensionFromUrl(string coverUrl)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(coverUrl, UriKind.Absolute).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 5)
                return extension;
        }
        catch
        {
        }

        return ".jpg";
    }

    private static void UpdateCoverMetadataFromFile(Game game, string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return;

            var frame = decoder.Frames[0];
            if (frame.PixelHeight > 0)
            {
                game.CoverAspectRatio = (double)frame.PixelWidth / frame.PixelHeight;
            }
        }
        catch
        {
        }
    }
}