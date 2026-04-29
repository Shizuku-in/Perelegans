using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<VndbRecommendationCacheDocument> LoadAsync()
    {
        await FileLock.WaitAsync();
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
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(VndbRecommendationCacheDocument document)
    {
        await FileLock.WaitAsync();
        try
        {
            document.EnsureInitialized();
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(CachePath, json);
        }
        finally
        {
            FileLock.Release();
        }
    }
}

public class VndbRecommendationCacheDocument
{
    public Dictionary<string, CachedVndbVisualNovel> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CachedRecommendationProfile? ProfileCache { get; set; }

    public void EnsureInitialized()
    {
        Entries ??= new Dictionary<string, CachedVndbVisualNovel>(StringComparer.OrdinalIgnoreCase);
        ProfileCache?.EnsureInitialized();
    }
}

public class CachedRecommendationProfile
{
    public string Signature { get; set; } = string.Empty;
    public DateTimeOffset CachedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Perelegans.Models.TasteProfileSummary Summary { get; set; } = new();
    public CachedRecommendationProfileData? Profile { get; set; }

    public void EnsureInitialized()
    {
        Summary ??= new Perelegans.Models.TasteProfileSummary();
        Profile?.EnsureInitialized();
    }
}

public class CachedRecommendationProfileData
{
    public Dictionary<string, double> TagScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> RecentTagScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PositiveTagIds { get; set; } = [];
    public List<string> SecondaryPositiveTagIds { get; set; } = [];
    public List<string> NegativeTagIds { get; set; } = [];
    public List<string> SoftNegativeTagIds { get; set; } = [];
    public Dictionary<string, double> DeveloperScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PreferredDevelopers { get; set; } = [];
    public double? PreferredReleaseYear { get; set; }
    public double MaxPositiveOverlap { get; set; }
    public double MaxRecentOverlap { get; set; }
    public List<CachedSourceGameProfile> SourceGames { get; set; } = [];
    public double DeveloperPreferenceWeight { get; set; }

    public void EnsureInitialized()
    {
        TagScores ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        RecentTagScores ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        PositiveTagIds ??= [];
        SecondaryPositiveTagIds ??= [];
        NegativeTagIds ??= [];
        SoftNegativeTagIds ??= [];
        DeveloperScores ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        PreferredDevelopers ??= [];
        SourceGames ??= [];

        foreach (var sourceGame in SourceGames)
            sourceGame.EnsureInitialized();
    }
}

public class CachedSourceGameProfile
{
    public string Title { get; set; } = string.Empty;
    public List<string> Developers { get; set; } = [];
    public DateTime? ReleaseDate { get; set; }
    public Dictionary<string, double> PositiveTagScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Weight { get; set; }
    public double RecentBias { get; set; }

    public void EnsureInitialized()
    {
        Developers ??= [];
        PositiveTagScores ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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
