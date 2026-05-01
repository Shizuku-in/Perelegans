using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class AiLibraryAssistantService
{
    private readonly AiRecommendationService _aiService;

    public AiLibraryAssistantService(HttpClient httpClient, SettingsService settingsService)
    {
        _aiService = new AiRecommendationService(httpClient, settingsService);
    }

    public bool IsConfigured => _aiService.IsConfigured;

    public async Task<string> AskAsync(string question, IReadOnlyCollection<Game> games)
    {
        if (TryAnswerLocally(question, games, out var localAnswer))
            return localAnswer;

        if (!_aiService.IsConfigured)
            return TranslationService.Instance["Assistant_NotConfigured"];

        if (string.IsNullOrWhiteSpace(question))
            return string.Empty;

        var lowerQuestion = question.ToLowerInvariant();
        var relevantGames = games
            .Select(game => new
            {
                Game = game,
                Score = ScoreQuestionMatch(lowerQuestion, game)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Game.AccessedDate)
            .Take(24)
            .Select(item => item.Game)
            .ToList();

        if (relevantGames.Count == 0)
            relevantGames = games.OrderByDescending(game => game.AccessedDate).Take(24).ToList();

        return await _aiService.AnswerLibraryQuestionAsync(question, relevantGames, BuildLibraryStats(games));
    }

    private static bool TryAnswerLocally(string question, IReadOnlyCollection<Game> games, out string answer)
    {
        answer = string.Empty;
        var normalized = question.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized is "test" or "\u6d4b\u8bd5")
        {
            answer = BuildLibraryOverview(games);
            return true;
        }

        if (ContainsAny(normalized, "\u5382\u5546", "\u54c1\u724c", "\u4f1a\u793e", "developer", "brand", "studio"))
        {
            var brands = games
                .Where(game => !string.IsNullOrWhiteSpace(game.Brand))
                .GroupBy(game => game.Brand.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Brand = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Brand, StringComparer.OrdinalIgnoreCase)
                .ToList();

            answer = brands.Count == 0
                ? "\u5f53\u524d\u5e93\u5185\u8fd8\u6ca1\u6709\u53ef\u7edf\u8ba1\u7684\u5382\u5546\u4fe1\u606f\u3002"
                : $"\u60a8\u7684\u6e38\u620f\u4e3b\u8981\u96c6\u4e2d\u5728{string.Join("\u3001", brands.Take(12).Select(item => $"{item.Brand}\uff08{item.Count}\u90e8\uff09"))}\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u7f3a\u5c01\u9762", "\u6ca1\u6709\u5c01\u9762", "missing cover", "no cover"))
        {
            var missing = games.Where(game => string.IsNullOrWhiteSpace(game.CoverDisplaySource)).ToList();
            answer = missing.Count == 0
                ? "\u5f53\u524d\u5e93\u5185\u6ca1\u6709\u7f3a\u5c01\u9762\u7684\u6e38\u620f\u3002"
                : $"\u5f53\u524d\u6709 {missing.Count} \u90e8\u6e38\u620f\u7f3a\u5c01\u9762\uff1a{string.Join("\u3001", missing.Take(12).Select(game => game.Title))}{(missing.Count > 12 ? " \u7b49" : string.Empty)}\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u7f3avndb", "\u6ca1\u6709vndb", "missing vndb"))
        {
            answer = $"\u5f53\u524d\u6709 {games.Count(game => string.IsNullOrWhiteSpace(game.VndbId))} \u90e8\u6e38\u620f\u7f3a VNDB ID\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u7f3abangumi", "\u6ca1\u6709bangumi", "missing bangumi"))
        {
            answer = $"\u5f53\u524d\u6709 {games.Count(game => string.IsNullOrWhiteSpace(game.BangumiId))} \u90e8\u6e38\u620f\u7f3a Bangumi ID\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u603b\u65f6\u957f", "\u6e38\u73a9\u65f6\u957f", "playtime", "time"))
        {
            var totalHours = games.Sum(game => game.Playtime.TotalHours);
            var top = games
                .OrderByDescending(game => game.Playtime)
                .Where(game => game.Playtime > TimeSpan.Zero)
                .Take(8)
                .Select(game => $"{game.Title}\uff08{FormatHours(game.Playtime.TotalHours)}\uff09")
                .ToList();
            answer = top.Count == 0
                ? $"\u603b\u8bb0\u5f55\u65f6\u957f\u4e3a {FormatHours(totalHours)}\u3002"
                : $"\u603b\u8bb0\u5f55\u65f6\u957f\u4e3a {FormatHours(totalHours)}\u3002\u65f6\u957f\u6700\u9ad8\u7684\u662f\uff1a{string.Join("\u3001", top)}\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u72b6\u6001", "\u5b8c\u6210", "\u901a\u5173", "\u8ba1\u5212", "\u5f03\u5751", "status", "completed", "planned", "dropped"))
        {
            answer = BuildLibraryOverview(games);
            return true;
        }

        if (ContainsAny(normalized, "\u6807\u7b7e", "tag", "tags"))
        {
            var tags = games
                .SelectMany(game => TagUtilities.Deserialize(game.Tags))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .GroupBy(tag => tag.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Tag = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList();
            answer = tags.Count == 0
                ? "\u5f53\u524d\u5e93\u5185\u8fd8\u6ca1\u6709\u53ef\u7edf\u8ba1\u7684\u6807\u7b7e\u3002"
                : $"\u51fa\u73b0\u6700\u591a\u7684\u6807\u7b7e\u662f\uff1a{string.Join("\u3001", tags.Select(item => $"{item.Tag}\uff08{item.Count}\uff09"))}\u3002";
            return true;
        }

        if (ContainsAny(normalized, "\u591a\u5c11", "\u51e0\u90e8", "\u603b\u6570", "count", "how many"))
        {
            answer = BuildLibraryOverview(games);
            return true;
        }

        return false;
    }

    private static string BuildLibraryOverview(IReadOnlyCollection<Game> games)
    {
        var completed = games.Count(game => game.Status == GameStatus.Completed);
        var playing = games.Count(game => game.Status == GameStatus.Playing);
        var planned = games.Count(game => game.Status == GameStatus.Planned);
        var dropped = games.Count(game => game.Status == GameStatus.Dropped);
        var totalHours = games.Sum(game => game.Playtime.TotalHours);
        return $"\u60a8\u7684\u89c6\u89c9\u5c0f\u8bf4\u5e93\u5171\u6709 {games.Count} \u90e8\u4f5c\u54c1\uff08\u5df2\u5b8c\u6210 {completed} \u90e8\u3001\u6e38\u73a9\u4e2d {playing} \u90e8\u3001\u8ba1\u5212\u4e2d {planned} \u90e8\u3001\u5df2\u5f03\u5751 {dropped} \u90e8\uff09\uff0c\u603b\u8bb0\u5f55\u65f6\u957f\u4e3a {FormatHours(totalHours)}\u3002";
    }

    private static string FormatHours(double hours)
    {
        return hours < 1 ? $"{Math.Round(hours * 60)} \u5206\u949f" : $"{hours:F1} \u5c0f\u65f6";
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static double ScoreQuestionMatch(string question, Game game)
    {
        var score = 0d;
        if (!string.IsNullOrWhiteSpace(game.Title) && question.Contains(game.Title.ToLowerInvariant(), StringComparison.Ordinal))
            score += 5;
        if (!string.IsNullOrWhiteSpace(game.Brand) && question.Contains(game.Brand.ToLowerInvariant(), StringComparison.Ordinal))
            score += 3;
        foreach (var tag in TagUtilities.Deserialize(game.Tags))
        {
            if (question.Contains(tag.ToLowerInvariant(), StringComparison.Ordinal))
                score += 2;
        }

        if (game.Status == GameStatus.Completed && (question.Contains("\u901a\u5173", StringComparison.Ordinal) || question.Contains("completed", StringComparison.Ordinal)))
            score += 1;
        if (game.Status == GameStatus.Dropped && (question.Contains("\u5f03", StringComparison.Ordinal) || question.Contains("dropped", StringComparison.Ordinal)))
            score += 1;
        if (!string.IsNullOrWhiteSpace(game.VndbId) || !string.IsNullOrWhiteSpace(game.BangumiId))
            score += 0.2;

        return score;
    }

    private static object BuildLibraryStats(IReadOnlyCollection<Game> games)
    {
        return new
        {
            total = games.Count,
            completed = games.Count(game => game.Status == GameStatus.Completed),
            playing = games.Count(game => game.Status == GameStatus.Playing),
            dropped = games.Count(game => game.Status == GameStatus.Dropped),
            planned = games.Count(game => game.Status == GameStatus.Planned),
            totalPlaytimeHours = Math.Round(games.Sum(game => game.Playtime.TotalHours), 1),
            missingVndb = games.Count(game => string.IsNullOrWhiteSpace(game.VndbId)),
            missingBangumi = games.Count(game => string.IsNullOrWhiteSpace(game.BangumiId)),
            missingCover = games.Count(game => string.IsNullOrWhiteSpace(game.CoverDisplaySource))
        };
    }
}
