using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Perelegans.Services;

public class VndbRecommendationCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perelegans",
        "cache");

    private static readonly string CachePath = Path.Combine(CacheDir, "vndb-recommendations.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<VndbRecommendationCacheDocument> LoadAsync()
    {
        try
        {
            if (!File.Exists(CachePath))
                return new VndbRecommendationCacheDocument();

            var json = await File.ReadAllTextAsync(CachePath);
            var document = JsonSerializer.Deserialize<VndbRecommendationCacheDocument>(json, JsonOptions)
                           ?? new VndbRecommendationCacheDocument();
            document.EnsureInitialized();
            return document;
        }
        catch
        {
            return new VndbRecommendationCacheDocument();
        }
    }

    public async Task SaveAsync(VndbRecommendationCacheDocument document)
    {
        document.EnsureInitialized();
        Directory.CreateDirectory(CacheDir);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(CachePath, json);
    }
}

public class VndbRecommendationCacheDocument
{
    public Dictionary<string, CachedVndbVisualNovel> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureInitialized()
    {
        Entries ??= new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);
    }
}

public class CachedVndbVisualNovel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public int? Rank { get; set; }
    public double? Rating { get; set; }
    public int? VoteCount { get; set; }
    public List<string> Developers { get; set; } = [];
    public List<CachedVndbTag> Tags { get; set; } = [];
    public List<CachedVndbExternalLink> ExternalLinks { get; set; } = [];
    public DateTimeOffset CachedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class CachedVndbTag
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Rating { get; set; }
}

public class CachedVndbExternalLink
{
    public string Url { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
