using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using HtmlAgilityPack;
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
        if (!string.IsNullOrWhiteSpace(game.VndbId))
        {
            var vndbCover = await TryResolveVndbCoverAsync(game.VndbId);
            if (!string.IsNullOrWhiteSpace(vndbCover))
                return vndbCover;
        }

        if (!string.IsNullOrWhiteSpace(game.BangumiId))
        {
            var bangumiCover = await TryResolveBangumiCoverAsync(game.BangumiId);
            if (!string.IsNullOrWhiteSpace(bangumiCover))
                return bangumiCover;
        }

        var bangumiSearchCover = await TryResolveBangumiCoverBySearchAsync(game.Title);
        if (!string.IsNullOrWhiteSpace(bangumiSearchCover))
            return bangumiSearchCover;

        var vndbSearchCover = await TryResolveVndbCoverBySearchAsync(game.Title);
        if (!string.IsNullOrWhiteSpace(vndbSearchCover))
            return vndbSearchCover;

        return null;
    }

    public async Task<IReadOnlyList<CoverCandidate>> GetCoverCandidatesAsync(
        string title,
        string? bangumiId,
        string? vndbId)
    {
        var candidates = new List<CoverCandidate>();
        var seenImageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vndbService = new VndbService(_httpClient);
        var bangumiService = new BangumiService(_httpClient);

        if (!string.IsNullOrWhiteSpace(vndbId))
        {
            var vndbCandidate = await vndbService.GetByIdAsync(vndbId);
            AddCandidate(candidates, seenImageUrls, vndbCandidate);
        }

        if (!string.IsNullOrWhiteSpace(bangumiId))
        {
            var bangumiCandidate = await bangumiService.GetByIdAsync(bangumiId);
            AddCandidate(candidates, seenImageUrls, bangumiCandidate);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalizedTitle = title.Trim();
            AddCandidates(candidates, seenImageUrls, await vndbService.SearchAsync(normalizedTitle));
            AddCandidates(candidates, seenImageUrls, await bangumiService.SearchAsync(normalizedTitle));

            foreach (var query in await GetCachedSearchAliasesAsync(normalizedTitle))
            {
                AddCandidates(candidates, seenImageUrls, await vndbService.SearchAsync(query));
                AddCandidates(candidates, seenImageUrls, await bangumiService.SearchAsync(query));
            }
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<string>> GetCachedSearchAliasesAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        try
        {
            var cacheService = new VndbRecommendationCacheService();
            var cache = await cacheService.LoadAsync();
            var key = RecommendationService.BuildTagWeightKey(title);
            if (!cache.SearchAliases.TryGetValue(key, out var aliases))
                return [];

            return aliases.Queries
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Select(query => query.Trim())
                .Where(query => !string.Equals(query, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task PopulateCandidatePreviewSourcesAsync(
        IReadOnlyList<CoverCandidate> candidates,
        string cacheKeyPrefix)
    {
        if (candidates.Count == 0)
            return;

        using var concurrencyLimiter = new SemaphoreSlim(4);
        var tasks = candidates.Select((candidate, index) => PopulateCandidatePreviewSourceAsync(
            candidate,
            $"{cacheKeyPrefix}-{index}",
            concurrencyLimiter));

        await Task.WhenAll(tasks);
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

        var pageCover = await TryResolveVndbCoverFromPageAsync(normalizedId);
        if (!string.IsNullOrWhiteSpace(pageCover))
            return pageCover;

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

    private async Task<string?> TryResolveVndbCoverFromPageAsync(string normalizedVndbId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://vndb.org/{normalizedVndbId}");
            request.Headers.Add("User-Agent", "Perelegans/0.2");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(html);

            var ogImage = document.DocumentNode
                .SelectSingleNode("//meta[@property='og:image']")
                ?.GetAttributeValue("content", string.Empty);
            var normalizedOgImage = NormalizeCoverUrl(ogImage);
            if (!string.IsNullOrWhiteSpace(normalizedOgImage))
                return normalizedOgImage;

            var mainImage = document.DocumentNode
                .SelectSingleNode("//main//img[@src]")
                ?.GetAttributeValue("src", string.Empty);
            var normalizedMainImage = NormalizeCoverUrl(mainImage);
            if (!string.IsNullOrWhiteSpace(normalizedMainImage))
                return normalizedMainImage;

            var fallbackImage = document.DocumentNode
                .SelectSingleNode("//img[@src]")
                ?.GetAttributeValue("src", string.Empty);
            return NormalizeCoverUrl(fallbackImage);
        }
        catch
        {
            return null;
        }
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
        if (!string.IsNullOrWhiteSpace(game.CoverImagePath) &&
            File.Exists(game.CoverImagePath))
        {
            UpdateCoverMetadataFromFile(game, game.CoverImagePath);
            return game.CoverImagePath;
        }

        if (!string.IsNullOrWhiteSpace(game.CoverImageUrl))
            return game.CoverImageUrl;

        var coverUrl = await ResolveCoverUrlAsync(game);
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        var cachedCover = await CacheCoverFromUrlAsync(coverUrl, $"game-{game.Id}");
        if (cachedCover == null)
            return null;

        if (!string.IsNullOrWhiteSpace(cachedCover.CachedPath))
        {
            game.CoverImageUrl = cachedCover.CoverUrl;
            game.CoverImagePath = cachedCover.CachedPath;
            if (cachedCover.AspectRatio.HasValue)
            {
                game.CoverAspectRatio = cachedCover.AspectRatio.Value;
            }
            else
            {
                UpdateCoverMetadataFromFile(game, cachedCover.CachedPath);
            }

            return cachedCover.CachedPath;
        }

        game.CoverImageUrl = cachedCover.CoverUrl;
        return cachedCover.CoverUrl;
    }

    public async Task<CoverArtFetchResult?> ResolveAndCacheCoverAsync(
        string title,
        string? bangumiId,
        string? vndbId,
        string cacheKey)
    {
        var coverUrl = await ResolveCoverUrlAsync(new Game
        {
            Title = title,
            BangumiId = bangumiId,
            VndbId = vndbId
        });

        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        return await CacheCoverFromUrlAsync(coverUrl, cacheKey);
    }

    public async Task<CoverArtFetchResult?> CacheCoverFromUrlAsync(string coverUrl, string cacheKey)
    {
        var normalizedCoverUrl = NormalizeCoverUrl(coverUrl);
        if (string.IsNullOrWhiteSpace(normalizedCoverUrl) ||
            !Uri.TryCreate(normalizedCoverUrl, UriKind.Absolute, out var coverUri))
        {
            return null;
        }

        if (coverUri.IsFile)
        {
            var localPath = coverUri.LocalPath;
            return File.Exists(localPath)
                ? new CoverArtFetchResult(normalizedCoverUrl, localPath, TryReadCoverAspectRatio(localPath))
                : null;
        }

        if (coverUri.Scheme != Uri.UriSchemeHttp && coverUri.Scheme != Uri.UriSchemeHttps)
            return null;

        var cachedPath = await DownloadCoverAsync(cacheKey, normalizedCoverUrl);
        var aspectRatio = TryReadCoverAspectRatio(cachedPath);
        return new CoverArtFetchResult(normalizedCoverUrl, cachedPath, aspectRatio);
    }

    public CoverArtFetchResult? ImportLocalCoverToCache(string localFilePath, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(localFilePath))
            return null;

        try
        {
            var sourcePath = Path.GetFullPath(localFilePath.Trim());
            if (!File.Exists(sourcePath))
                return null;

            Directory.CreateDirectory(CoverCacheDir);

            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
            {
                extension = ".jpg";
            }

            var sanitizedKey = SanitizeCacheKey(cacheKey);
            var cachedPath = Path.Combine(CoverCacheDir, BuildVersionedCacheFileName(sanitizedKey, extension));

            if (!string.Equals(sourcePath, cachedPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, cachedPath, overwrite: true);
            }

            var aspectRatio = TryReadCoverAspectRatio(cachedPath);
            if (!aspectRatio.HasValue)
                return null;

            return new CoverArtFetchResult(new Uri(cachedPath, UriKind.Absolute).AbsoluteUri, cachedPath, aspectRatio);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> DownloadCoverAsync(string cacheKey, string coverUrl)
    {
        try
        {
            Directory.CreateDirectory(CoverCacheDir);

            var extension = GetExtensionFromUrl(coverUrl);
            var sanitizedKey = SanitizeCacheKey(cacheKey);
            var filePath = Path.Combine(CoverCacheDir, BuildVersionedCacheFileName(sanitizedKey, extension));

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

    public static double? TryReadCoverAspectRatio(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;

            var frame = decoder.Frames[0];
            return frame.PixelHeight > 0
                ? (double)frame.PixelWidth / frame.PixelHeight
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateCoverMetadataFromFile(Game game, string filePath)
    {
        var aspectRatio = TryReadCoverAspectRatio(filePath);
        if (aspectRatio.HasValue)
        {
            game.CoverAspectRatio = aspectRatio.Value;
        }
    }

    private static void AddCandidates(
        ICollection<CoverCandidate> candidates,
        ISet<string> seenImageUrls,
        IEnumerable<MetadataResult> results)
    {
        foreach (var result in results)
            AddCandidate(candidates, seenImageUrls, result);
    }

    private static void AddCandidate(
        ICollection<CoverCandidate> candidates,
        ISet<string> seenImageUrls,
        MetadataResult? result)
    {
        var imageUrl = NormalizeCoverUrl(result?.ImageUrl);
        if (string.IsNullOrWhiteSpace(imageUrl) || !seenImageUrls.Add(imageUrl))
            return;

        result!.ImageUrl = imageUrl;
        candidates.Add(CoverCandidate.FromMetadataResult(result));
    }

    private async Task PopulateCandidatePreviewSourceAsync(
        CoverCandidate candidate,
        string cacheKey,
        SemaphoreSlim concurrencyLimiter)
    {
        var normalizedImageUrl = NormalizeCoverUrl(candidate.ImageUrl);
        candidate.PreviewSource = normalizedImageUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedImageUrl))
            return;

        await concurrencyLimiter.WaitAsync();
        try
        {
            var cachedCover = await CacheCoverFromUrlAsync(normalizedImageUrl, cacheKey);
            if (!string.IsNullOrWhiteSpace(cachedCover?.CachedPath))
            {
                candidate.PreviewSource = cachedCover.CachedPath!;
                return;
            }

            if (!string.IsNullOrWhiteSpace(cachedCover?.CoverUrl))
            {
                candidate.PreviewSource = cachedCover.CoverUrl;
            }
        }
        catch
        {
            candidate.PreviewSource = normalizedImageUrl;
        }
        finally
        {
            concurrencyLimiter.Release();
        }
    }

    private static string? NormalizeCoverUrl(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        var trimmed = coverUrl.Trim();
        var normalized = trimmed.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{trimmed}"
            : trimmed;

        normalized = normalized
            .Replace("https://t.vndb.org/cv.t/", "https://t.vndb.org/cv/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://t.vndb.org/cv.s/", "https://t.vndb.org/cv/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://t.vndb.org/ch.t/", "https://t.vndb.org/ch/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://t.vndb.org/ch.s/", "https://t.vndb.org/ch/", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }

    private static string SanitizeCacheKey(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return "cover";

        var sanitized = cacheKey.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized)
            ? "cover"
            : sanitized;
    }

    private static string BuildVersionedCacheFileName(string sanitizedKey, string extension)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension) || extension.Length > 5
            ? ".jpg"
            : extension;

        return $"{sanitizedKey}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{normalizedExtension}";
    }
}

public sealed record CoverArtFetchResult(string CoverUrl, string? CachedPath, double? AspectRatio);




