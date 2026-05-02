using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public sealed class BangumiSyncService
{
    private readonly HttpClient _httpClient;
    private readonly BangumiService _bangumiService;
    private readonly DatabaseService _dbService;
    private readonly SettingsService _settingsService;

    public BangumiSyncService(HttpClient httpClient, DatabaseService dbService, SettingsService settingsService)
    {
        _httpClient = httpClient;
        _bangumiService = new BangumiService(httpClient);
        _dbService = dbService;
        _settingsService = settingsService;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settingsService.Settings.BangumiAccessToken);

    public async Task<int> PullCollectionsAsync(IReadOnlyCollection<Game> games, CancellationToken cancellationToken = default)
    {
        var result = await PullCollectionsDetailedAsync(games, cancellationToken);
        return result.ChangedCount;
    }

    public async Task<BangumiCollectionSyncResult> PullCollectionsDetailedAsync(
        IReadOnlyCollection<Game> games,
        CancellationToken cancellationToken = default)
    {
        var token = await GetUsableAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            return new BangumiCollectionSyncResult(0, 0, 0, 0);

        var candidates = games
            .Where(game => !string.IsNullOrWhiteSpace(game.BangumiId))
            .ToList();

        var collections = await _bangumiService.GetGameCollectionsAsync(token);
        var collectionMap = collections
            .GroupBy(collection => collection.SubjectId)
            .ToDictionary(group => group.Key, group => group.First());

        var changedCount = 0;
        var foundCount = 0;
        foreach (var game in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!int.TryParse(game.BangumiId, out var subjectId) ||
                !collectionMap.TryGetValue(subjectId, out var collection))
            {
                continue;
            }

            foundCount++;

            if (ApplyCollection(game, collection))
            {
                await _dbService.UpdateGameAsync(game);
                changedCount++;
            }
        }

        return new BangumiCollectionSyncResult(candidates.Count, collections.Count, foundCount, changedCount);
    }

    public async Task<BangumiCollectionState?> GetCollectionStateAsync(string? bangumiId, CancellationToken cancellationToken = default)
    {
        var token = await GetUsableAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(bangumiId))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        return await _bangumiService.GetCollectionAsync(bangumiId, token);
    }

    public async Task<bool> PushCollectionAsync(Game game, CancellationToken cancellationToken = default)
    {
        var token = await GetUsableAccessTokenAsync(cancellationToken);
        System.Diagnostics.Debug.WriteLine($"BangumiSyncService.PushCollectionAsync: Token={(string.IsNullOrWhiteSpace(token) ? "empty" : $"present ({token.Length} chars)")}, BangumiId={game.BangumiId}");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(game.BangumiId))
            return false;

        try
        {
            var result = await _bangumiService.UpdateCollectionAsync(
                game.BangumiId,
                token,
                game.Status,
                game.BangumiRating,
                game.BangumiComment,
                cancellationToken);
            System.Diagnostics.Debug.WriteLine($"BangumiSyncService.PushCollectionAsync: API result={result}");
            return result;
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"BangumiSyncService.PushCollectionAsync: Timeout - {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BangumiSyncService.PushCollectionAsync: Error - {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<string> GetUsableAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Settings;
        if (string.IsNullOrWhiteSpace(settings.BangumiAccessToken))
            return string.Empty;

        var shouldRefresh = settings.BangumiAccessTokenExpiresAt.HasValue &&
                            settings.BangumiAccessTokenExpiresAt.Value <= DateTime.Now.AddMinutes(5);
        if (!shouldRefresh)
            return settings.BangumiAccessToken.Trim();

        if (string.IsNullOrWhiteSpace(settings.BangumiRefreshToken) ||
            string.IsNullOrWhiteSpace(settings.BangumiClientId) ||
            string.IsNullOrWhiteSpace(settings.BangumiClientSecret))
        {
            return settings.BangumiAccessToken.Trim();
        }

        var oauthService = new BangumiOAuthService(_httpClient);
        var refreshed = await oauthService.RefreshAsync(
            settings.BangumiClientId,
            settings.BangumiClientSecret,
            settings.BangumiRefreshToken,
            cancellationToken);

        settings.BangumiAccessToken = refreshed.AccessToken;
        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            settings.BangumiRefreshToken = refreshed.RefreshToken;
        settings.BangumiAccessTokenExpiresAt = refreshed.ExpiresAt;
        _settingsService.Save();

        return settings.BangumiAccessToken.Trim();
    }

    private static bool ApplyCollection(Game game, BangumiCollectionState collection)
    {
        var changed = false;
        var status = BangumiService.MapCollectionTypeToGameStatus(collection.Type);

        changed |= SetIfChanged(game.Status, status, value => game.Status = value);
        changed |= SetIfChanged(game.BangumiCollectionType, collection.Type, value => game.BangumiCollectionType = value);
        changed |= SetIfChanged(game.BangumiRating, collection.Rating, value => game.BangumiRating = value);
        changed |= SetIfChanged(game.BangumiComment, collection.Comment, value => game.BangumiComment = value);
        changed |= SetIfChanged(game.BangumiCollectionUpdatedAt, collection.UpdatedAt, value => game.BangumiCollectionUpdatedAt = value);

        game.BangumiLastSyncedAt = DateTime.Now;
        return changed;
    }

    private static bool SetIfChanged<T>(T current, T next, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
            return false;

        apply(next);
        return true;
    }
}

public sealed record BangumiCollectionSyncResult(int CandidateCount, int FetchedCount, int FoundCount, int ChangedCount);
