using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public class AiLibraryAssistantService
{
    private const int AiMatchedContextLimit = 24;
    private const int AiFallbackContextLimit = 12;
    private readonly AiRecommendationService _aiService;

    public AiLibraryAssistantService(HttpClient httpClient, SettingsService settingsService)
    {
        _aiService = new AiRecommendationService(httpClient, settingsService);
    }

    public bool IsConfigured => _aiService.IsConfigured;

    public async Task<AiAssistantResponse> AskAsync(
        string question,
        IReadOnlyCollection<Game> games,
        IReadOnlyList<AiAssistantMessage> recentMessages,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuestion = question.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
            return new AiAssistantResponse();

        if (TryResolveGameOpinionQuestion(normalizedQuestion, games, recentMessages, out var opinionGame))
        {
            return await AnswerGameOpinionQuestionAsync(question, opinionGame, games, recentMessages, cancellationToken);
        }

        if (TryAnswerLocally(question, games, recentMessages, out var localResponse))
        {
            if (_aiService.IsConfigured && localResponse.ActionKind == AiAssistantActionKind.None)
            {
                var linkedIds = localResponse.GameLinks.Select(link => link.GameId).ToHashSet();
                var polishGames = linkedIds.Count == 0
                    ? games.OrderByDescending(game => game.AccessedDate).Take(AiFallbackContextLimit).ToList()
                    : games.Where(game => linkedIds.Contains(game.Id)).Take(AiMatchedContextLimit).ToList();
                var polishPrompt =
                    $"请只润色这段本地工具已经精确计算出的回答，不要新增事实，不要改变数字和游戏名。原问题：{question}\n本地结果：{localResponse.Answer}";
                var polished = await _aiService.AnswerLibraryQuestionAsync(
                    polishPrompt,
                    polishGames,
                    BuildLibraryStats(games),
                    recentMessages,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(polished))
                {
                    localResponse.Answer = polished;
                    localResponse.UsedAi = true;
                    localResponse.DebugSummary = $"{localResponse.DebugSummary}; AI: polish";
                }
            }

            return localResponse;
        }

        if (!_aiService.IsConfigured)
        {
            return new AiAssistantResponse
            {
                Answer = TranslationService.Instance["Assistant_NotConfigured"],
                SourceSummary = BuildSourceSummary(games, "AI configuration"),
                DebugSummary = "Intent: AI fallback; local tool: none; AI: unavailable"
            };
        }

        var lowerQuestion = normalizedQuestion;
        var scoredGames = games
            .Select(game => new
            {
                Game = game,
                Score = ScoreQuestionMatch(lowerQuestion, game)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Game.AccessedDate)
            .ToList();

        var relevantGames = scoredGames
            .Where(item => item.Score > 0)
            .Take(AiMatchedContextLimit)
            .Select(item => item.Game)
            .ToList();

        var contextDescription = "matched title, brand, status, playtime, IDs, cover and tag fields";
        if (relevantGames.Count == 0)
        {
            relevantGames = games
                .OrderByDescending(game => game.AccessedDate)
                .Take(AiFallbackContextLimit)
                .ToList();
            contextDescription = "recent games, status, playtime, IDs, cover and tag fields";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var answer = await _aiService.AnswerLibraryQuestionAsync(
            question,
            relevantGames,
            BuildLibraryStats(games),
            recentMessages,
            cancellationToken);
        return new AiAssistantResponse
        {
            Answer = string.IsNullOrWhiteSpace(answer) ? TranslationService.Instance["Assistant_NoAnswer"] : answer,
            SourceSummary = BuildSourceSummary(relevantGames, contextDescription),
            DebugSummary = $"Intent: AI fallback; local tool: relevance search; AI: called; games: {relevantGames.Count}",
            UsedAi = true
        };
    }

    private async Task<AiAssistantResponse> AnswerGameOpinionQuestionAsync(
        string question,
        Game game,
        IReadOnlyCollection<Game> games,
        IReadOnlyList<AiAssistantMessage> recentMessages,
        CancellationToken cancellationToken)
    {
        if (!_aiService.IsConfigured)
        {
            return BuildGameListResponse(
                [game],
                $"{BuildGameDetails(game)} AI 尚未配置，因此这里只能展示本地元数据，无法给出更完整的评价。",
                "title, brand, status, playtime, IDs, cover and tag fields",
                "game opinion fallback");
        }

        var answer = await _aiService.AnswerLibraryQuestionAsync(
            $"{question}\n请基于提供的本地元数据评价这部作品适合什么口味、目前数据能说明什么、还缺哪些信息。不要编造剧情细节。",
            [game],
            BuildLibraryStats(games),
            recentMessages,
            cancellationToken);

        var response = BuildGameListResponse(
            [game],
            string.IsNullOrWhiteSpace(answer) ? TranslationService.Instance["Assistant_NoAnswer"] : answer,
            "one matched game's title, brand, status, playtime, IDs, cover and tag fields",
            "game opinion");
        response.UsedAi = !string.IsNullOrWhiteSpace(answer);
        response.DebugSummary = $"{response.DebugSummary}; AI: {(response.UsedAi ? "called" : "empty")}";
        return response;
    }

    private static bool TryAnswerLocally(
        string question,
        IReadOnlyCollection<Game> games,
        IReadOnlyList<AiAssistantMessage> recentMessages,
        out AiAssistantResponse response)
    {
        response = new AiAssistantResponse();
        var normalized = question.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var contextGames = ResolveContextGames(normalized, recentMessages, games);

        if (normalized is "test" or "测试")
        {
            response = BuildResponse(BuildLibraryOverview(games), games, "total count, status fields, total playtime", "overview", usedGames: []);
            return true;
        }

        if (ContainsAny(normalized, "打开第一个", "第一个", "first one", "open first"))
        {
            var first = contextGames.FirstOrDefault();
            if (first == null)
                return false;

            response = BuildResponse($"已找到第一个结果：{first.Title}。", [first], "previous assistant result list", "follow-up select first", [first]);
            return true;
        }

        if (ContainsAny(normalized, "这些", "其中", "these", "those") &&
            ContainsAny(normalized, "缺封面", "没有封面", "missing cover", "no cover"))
        {
            var missing = contextGames.Where(game => string.IsNullOrWhiteSpace(game.CoverDisplaySource)).ToList();
            response = BuildGameListResponse(missing, $"这些结果里有 {missing.Count} 部缺封面。", "previous result list and cover fields", "missing cover follow-up");
            return true;
        }

        if (TryBuildNaturalLanguageFilter(normalized, games, out var filterGames, out var filterDescription))
        {
            response = BuildGameListResponse(
                filterGames,
                filterGames.Count == 0 ? "没有找到符合条件的作品。" : $"已筛出 {filterGames.Count} 部作品：{filterDescription}。",
                "brand, status and tag fields",
                "natural language filter");
            response.ActionKind = AiAssistantActionKind.FilterGames;
            response.ActionLabel = "只显示这些游戏";
            return true;
        }

        if (ContainsAny(normalized, "可能缺 bangumi", "缺 bangumi", "没有bangumi", "missing bangumi", "no bangumi"))
        {
            var missing = games.Where(game => string.IsNullOrWhiteSpace(game.BangumiId)).OrderByDescending(game => game.AccessedDate).ToList();
            response = BuildGameListResponse(
                missing,
                missing.Count == 0 ? "当前库内没有缺 Bangumi ID 的游戏。" : $"当前有 {missing.Count} 部游戏缺 Bangumi ID，下面是待确认列表。",
                "Bangumi ID field",
                "draft missing Bangumi");
            response.ActionKind = AiAssistantActionKind.DraftBangumiLookup;
            response.ActionLabel = "确认后逐个补全";
            return true;
        }

        if (ContainsAny(normalized, "缺vndb", "没有vndb", "missing vndb", "no vndb"))
        {
            var missing = games.Where(game => string.IsNullOrWhiteSpace(game.VndbId)).OrderByDescending(game => game.AccessedDate).ToList();
            response = BuildGameListResponse(
                missing,
                missing.Count == 0 ? "当前库内没有缺 VNDB ID 的游戏。" : $"当前有 {missing.Count} 部游戏缺 VNDB ID。",
                "VNDB ID field",
                "missing VNDB");
            return true;
        }

        if (ContainsAny(normalized, "缺封面", "没有封面", "missing cover", "no cover"))
        {
            var missing = games.Where(game => string.IsNullOrWhiteSpace(game.CoverDisplaySource)).OrderByDescending(game => game.AccessedDate).ToList();
            response = BuildGameListResponse(
                missing,
                missing.Count == 0 ? "当前库内没有缺封面的游戏。" : $"当前有 {missing.Count} 部游戏缺封面。",
                "cover path and cover URL fields",
                "missing cover");
            return true;
        }

        if (ContainsAny(normalized, "重复标题", "重名", "duplicate title", "duplicates"))
        {
            var duplicates = games
                .Where(game => !string.IsNullOrWhiteSpace(game.Title))
                .GroupBy(game => game.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group)
                .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            response = BuildGameListResponse(
                duplicates,
                duplicates.Count == 0 ? "没有发现重复标题。" : $"发现 {duplicates.Count} 条可能重复的标题记录。",
                "title field",
                "duplicate titles");
            return true;
        }

        if (ContainsAny(normalized, "最近玩", "最近游玩", "recently played", "last played"))
        {
            var recent = games.OrderByDescending(game => game.AccessedDate).Take(12).ToList();
            response = BuildGameListResponse(recent, recent.Count == 0 ? "当前库内没有游戏。" : "最近游玩的作品如下。", "accessed date and playtime fields", "recently played");
            return true;
        }

        if (ContainsAny(normalized, "厂商", "品牌", "会社", "developer", "brand", "studio"))
        {
            var brands = games
                .Where(game => !string.IsNullOrWhiteSpace(game.Brand))
                .GroupBy(game => game.Brand.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Brand = group.Key, Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Brand, StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList();

            var answer = brands.Count == 0
                ? "当前库内还没有可统计的厂商信息。"
                : $"厂商分布最高的是：{string.Join("、", brands.Select(item => $"{item.Brand}（{item.Count} 部）"))}。";
            response = BuildResponse(answer, games, "brand field", "brand distribution", usedGames: []);
            return true;
        }

        if (ContainsAny(normalized, "总时长", "游玩时长", "playtime", "time"))
        {
            var totalHours = games.Sum(game => game.Playtime.TotalHours);
            var top = games
                .OrderByDescending(game => game.Playtime)
                .Where(game => game.Playtime > TimeSpan.Zero)
                .Take(8)
                .ToList();
            var answer = top.Count == 0
                ? $"总记录时长为 {FormatHours(totalHours)}。"
                : $"总记录时长为 {FormatHours(totalHours)}。时长最高的是：{string.Join("、", top.Select(game => $"{game.Title}（{FormatHours(game.Playtime.TotalHours)}）"))}。";
            response = BuildGameListResponse(top, answer, "playtime field", "playtime statistics");
            return true;
        }

        if (ContainsAny(normalized, "标签", "tag", "tags"))
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
            var answer = tags.Count == 0
                ? "当前库内还没有可统计的标签。"
                : $"出现最多的标签是：{string.Join("、", tags.Select(item => $"{item.Tag}（{item.Count}）"))}。";
            response = BuildResponse(answer, games, "tags field", "tag statistics", usedGames: []);
            return true;
        }

        var mentionedGame = FindMentionedGame(normalized, games);
        if (mentionedGame != null)
        {
            response = BuildGameListResponse([mentionedGame], BuildGameDetails(mentionedGame), "title, brand, status, playtime, IDs, cover and tag fields", "game details");
            return true;
        }

        if (ContainsAny(normalized, "状态", "完成", "通关", "计划", "弃坑", "多少", "几部", "总数", "status", "completed", "planned", "dropped", "count", "how many"))
        {
            response = BuildResponse(BuildLibraryOverview(games), games, "total count, status fields, total playtime", "overview", usedGames: []);
            return true;
        }

        return false;
    }

    private static AiAssistantResponse BuildGameListResponse(IReadOnlyList<Game> games, string answer, string sourceFields, string intent)
    {
        return BuildResponse(answer, games, sourceFields, intent, games.Take(24));
    }

    private static AiAssistantResponse BuildResponse(string answer, IReadOnlyCollection<Game> sourceGames, string sourceFields, string intent, IEnumerable<Game> usedGames)
    {
        var response = new AiAssistantResponse
        {
            Answer = answer,
            SourceSummary = BuildSourceSummary(sourceGames, sourceFields),
            DebugSummary = $"Intent: {intent}; local tool: used; AI: not called; games: {sourceGames.Count}",
            UsedLocalTool = true
        };

        foreach (var game in usedGames)
            response.GameLinks.Add(ToGameLink(game));

        return response;
    }

    private static AiAssistantGameLink ToGameLink(Game game)
    {
        var subtitleParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.Brand))
            subtitleParts.Add(game.Brand);
        subtitleParts.Add(StatusText(game.Status));
        if (game.Playtime > TimeSpan.Zero)
            subtitleParts.Add(PlaytimeTextFormatter.Format(game.Playtime));

        return new AiAssistantGameLink
        {
            GameId = game.Id,
            Title = game.Title,
            Subtitle = string.Join(" · ", subtitleParts)
        };
    }

    private static IReadOnlyList<Game> ResolveContextGames(string normalized, IReadOnlyList<AiAssistantMessage> recentMessages, IReadOnlyCollection<Game> games)
    {
        if (!ContainsAny(normalized, "这些", "其中", "第一", "这个游戏", "这游戏", "这部", "它", "these", "those", "first", "this game", "it"))
            return [];

        var ids = recentMessages
            .Where(message => !message.IsUser && message.GameLinks.Count > 0)
            .Reverse()
            .SelectMany(message => message.GameLinks.Select(link => link.GameId))
            .Distinct()
            .Take(48)
            .ToHashSet();

        return games.Where(game => ids.Contains(game.Id)).ToList();
    }

    private static bool TryResolveGameOpinionQuestion(
        string normalized,
        IReadOnlyCollection<Game> games,
        IReadOnlyList<AiAssistantMessage> recentMessages,
        out Game game)
    {
        game = null!;
        if (!IsGameOpinionQuestion(normalized))
            return false;

        var mentionedGame = FindMentionedGame(normalized, games);
        if (mentionedGame != null)
        {
            game = mentionedGame;
            return true;
        }

        if (!ContainsAny(normalized, "这个游戏", "这游戏", "这部", "它", "this game", "it"))
            return false;

        var contextGame = ResolveContextGames(normalized, recentMessages, games).FirstOrDefault();
        if (contextGame == null)
            return false;

        game = contextGame;
        return true;
    }

    private static bool IsGameOpinionQuestion(string normalized)
    {
        return ContainsAny(
            normalized,
            "怎么样",
            "如何",
            "评价",
            "点评",
            "值得玩吗",
            "值得玩",
            "推荐吗",
            "好玩吗",
            "what do you think",
            "how is",
            "is it worth",
            "worth playing");
    }

    private static bool TryBuildNaturalLanguageFilter(string normalized, IReadOnlyCollection<Game> games, out List<Game> filteredGames, out string description)
    {
        filteredGames = [];
        description = string.Empty;

        if (!ContainsAny(normalized, "显示", "筛选", "show", "filter"))
            return false;

        IEnumerable<Game> query = games;
        var matchedBrand = games
            .Select(game => game.Brand)
            .Where(brand => !string.IsNullOrWhiteSpace(brand))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(brand => normalized.Contains(brand!.ToLowerInvariant(), StringComparison.Ordinal));
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(matchedBrand))
        {
            query = query.Where(game => string.Equals(game.Brand, matchedBrand, StringComparison.OrdinalIgnoreCase));
            parts.Add(matchedBrand!);
        }

        if (ContainsAny(normalized, "已通关", "已完成", "completed"))
        {
            query = query.Where(game => game.Status == GameStatus.Completed);
            parts.Add("已通关");
        }
        else if (ContainsAny(normalized, "游玩中", "在玩", "playing"))
        {
            query = query.Where(game => game.Status == GameStatus.Playing);
            parts.Add("游玩中");
        }
        else if (ContainsAny(normalized, "计划", "planned"))
        {
            query = query.Where(game => game.Status == GameStatus.Planned);
            parts.Add("计划中");
        }
        else if (ContainsAny(normalized, "弃坑", "dropped"))
        {
            query = query.Where(game => game.Status == GameStatus.Dropped);
            parts.Add("已弃坑");
        }

        var matchedTag = games
            .SelectMany(game => TagUtilities.Deserialize(game.Tags))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(tag => normalized.Contains(tag.ToLowerInvariant(), StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(matchedTag))
        {
            query = query.Where(game => TagUtilities.Deserialize(game.Tags).Any(tag => string.Equals(tag, matchedTag, StringComparison.OrdinalIgnoreCase)));
            parts.Add(matchedTag);
        }

        if (parts.Count == 0)
            return false;

        filteredGames = query.OrderByDescending(game => game.AccessedDate).ToList();
        description = string.Join("、", parts);
        return true;
    }

    private static Game? FindMentionedGame(string normalized, IReadOnlyCollection<Game> games)
    {
        return games
            .Where(game => !string.IsNullOrWhiteSpace(game.Title) && normalized.Contains(game.Title.ToLowerInvariant(), StringComparison.Ordinal))
            .OrderByDescending(game => game.Title.Length)
            .FirstOrDefault();
    }

    private static string BuildGameDetails(Game game)
    {
        var tags = TagUtilities.Deserialize(game.Tags).Take(8).ToList();
        return $"{game.Title}：厂商 {DisplayValue(game.Brand)}，状态 {StatusText(game.Status)}，游玩时长 {PlaytimeTextFormatter.Format(game.Playtime)}，VNDB {DisplayValue(game.VndbId)}，Bangumi {DisplayValue(game.BangumiId)}，封面 {(string.IsNullOrWhiteSpace(game.CoverDisplaySource) ? "缺失" : "已设置")}{(tags.Count > 0 ? $"，标签 {string.Join("、", tags)}" : string.Empty)}。";
    }

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未设置" : value;
    }

    private static string BuildLibraryOverview(IReadOnlyCollection<Game> games)
    {
        var completed = games.Count(game => game.Status == GameStatus.Completed);
        var playing = games.Count(game => game.Status == GameStatus.Playing);
        var planned = games.Count(game => game.Status == GameStatus.Planned);
        var dropped = games.Count(game => game.Status == GameStatus.Dropped);
        var totalHours = games.Sum(game => game.Playtime.TotalHours);
        return $"你的视觉小说库共有 {games.Count} 部作品（已完成 {completed} 部、游玩中 {playing} 部、计划中 {planned} 部、已弃坑 {dropped} 部），总记录时长为 {FormatHours(totalHours)}。";
    }

    private static string BuildSourceSummary(IReadOnlyCollection<Game> games, string fields)
    {
        return $"依据：{games.Count} 部游戏的 {fields}。";
    }

    private static string FormatHours(double hours)
    {
        return hours < 1 ? $"{Math.Round(hours * 60)} 分钟" : $"{hours:F1} 小时";
    }

    private static string StatusText(GameStatus status)
    {
        return status switch
        {
            GameStatus.Completed => "已通关",
            GameStatus.Playing => "游玩中",
            GameStatus.Planned => "计划中",
            GameStatus.Dropped => "已弃坑",
            _ => status.ToString()
        };
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

        if (game.Status == GameStatus.Completed && (question.Contains("通关", StringComparison.Ordinal) || question.Contains("completed", StringComparison.Ordinal)))
            score += 1;
        if (game.Status == GameStatus.Dropped && (question.Contains("弃", StringComparison.Ordinal) || question.Contains("dropped", StringComparison.Ordinal)))
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
