using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class AiAssistantViewModel : ObservableObject
{
    private const int ContextMessageCount = 10;
    private readonly AiLibraryAssistantService _assistantService;
    private readonly IReadOnlyCollection<Game> _games;
    private readonly Action<int>? _selectGame;
    private readonly Action<int>? _openGameMetadata;
    private readonly Action<IReadOnlyCollection<int>>? _applyGameFilter;
    private readonly Dictionary<string, AiAssistantResponse> _responseCache = new();
    private CancellationTokenSource? _sendCancellation;

    [ObservableProperty]
    private ObservableCollection<AiAssistantMessage> _messages = new();

    [ObservableProperty]
    private ObservableCollection<string> _quickQuestions = new(
    [
        "缺封面的游戏",
        "缺 Bangumi ID",
        "最近玩了什么",
        "厂商分布",
        "标签统计"
    ]);

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyStatusText = string.Empty;

    public bool HasBusyStatus => !string.IsNullOrWhiteSpace(BusyStatusText);
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText);
    public bool CanCancel => IsBusy;

    public AiAssistantViewModel(
        HttpClient httpClient,
        SettingsService settingsService,
        IReadOnlyCollection<Game> games,
        Action<int>? selectGame = null,
        Action<int>? openGameMetadata = null,
        Action<IReadOnlyCollection<int>>? applyGameFilter = null)
    {
        _assistantService = new AiLibraryAssistantService(httpClient, settingsService);
        _games = games;
        _selectGame = selectGame;
        _openGameMetadata = openGameMetadata;
        _applyGameFilter = applyGameFilter;
        Messages.Add(new AiAssistantMessage
        {
            Role = "assistant",
            Content = TranslationService.Instance["Assistant_Welcome"]
        });
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task Send()
    {
        await SendQuestionAsync(InputText.Trim());
    }

    [RelayCommand]
    private async Task AskQuickQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || IsBusy)
            return;

        await SendQuestionAsync(question.Trim());
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _sendCancellation?.Cancel();
    }

    [RelayCommand]
    private void SelectGame(AiAssistantGameLink? link)
    {
        if (link == null)
            return;

        _selectGame?.Invoke(link.GameId);
    }

    [RelayCommand]
    private void OpenGameMetadata(AiAssistantGameLink? link)
    {
        if (link == null)
            return;

        _openGameMetadata?.Invoke(link.GameId);
    }

    [RelayCommand]
    private void RunMessageAction(AiAssistantMessage? message)
    {
        if (message == null || message.GameLinks.Count == 0)
            return;

        var ids = message.GameLinks.Select(link => link.GameId).Distinct().ToList();
        if (message.ActionKind is AiAssistantActionKind.FilterGames or AiAssistantActionKind.DraftBangumiLookup)
            _applyGameFilter?.Invoke(ids);
    }

    private async Task SendQuestionAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || IsBusy)
            return;

        InputText = string.Empty;
        Messages.Add(new AiAssistantMessage { Role = "user", Content = question });

        IsBusy = true;
        _sendCancellation = new CancellationTokenSource();
        var token = _sendCancellation.Token;

        try
        {
            BusyStatusText = "识别问题意图";
            await Task.Delay(90, token);

            BusyStatusText = "查询本地库";
            var recentMessages = Messages.TakeLast(ContextMessageCount).ToList();
            var cacheKey = BuildCacheKey(question, _games);
            var response = _responseCache.TryGetValue(cacheKey, out var cachedResponse)
                ? CloneResponse(cachedResponse)
                : await _assistantService.AskAsync(question, _games, recentMessages, token);

            if (cachedResponse != null)
            {
                response.DebugSummary = string.IsNullOrWhiteSpace(response.DebugSummary)
                    ? "Cache: hit"
                    : $"{response.DebugSummary}; cache: hit";
            }
            else
            {
                _responseCache[cacheKey] = CloneResponse(response);
            }

            BusyStatusText = "整理结果";
            var assistantMessage = BuildAssistantMessage(response);
            Messages.Add(assistantMessage);

            BusyStatusText = response.UsedAi ? "生成回答" : "本地结果流式输出";
            var finalAnswer = string.IsNullOrWhiteSpace(response.Answer)
                ? TranslationService.Instance["Assistant_NoAnswer"]
                : response.Answer;
            await StreamContentAsync(assistantMessage, finalAnswer, token);
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new AiAssistantMessage
            {
                Role = "assistant",
                Content = "已取消本次请求。"
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new AiAssistantMessage
            {
                Role = "assistant",
                Content = ex.Message
            });
        }
        finally
        {
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            BusyStatusText = string.Empty;
            IsBusy = false;
        }
    }

    private static AiAssistantMessage BuildAssistantMessage(AiAssistantResponse response)
    {
        var message = new AiAssistantMessage
        {
            Role = "assistant",
            SourceSummary = response.SourceSummary,
            DebugSummary = response.DebugSummary,
            ActionKind = response.ActionKind,
            ActionLabel = response.ActionLabel
        };

        foreach (var link in response.GameLinks)
            message.GameLinks.Add(link);

        return message;
    }

    private static string BuildCacheKey(string question, IReadOnlyCollection<Game> games)
    {
        var maxAccessedTicks = games.Count == 0 ? 0 : games.Max(game => game.AccessedDate.Ticks);
        var totalPlaytimeTicks = games.Sum(game => game.Playtime.Ticks);
        var metadataState = games.Sum(game =>
            (string.IsNullOrWhiteSpace(game.CoverDisplaySource) ? 1 : 0)
            + (string.IsNullOrWhiteSpace(game.BangumiId) ? 3 : 0)
            + (string.IsNullOrWhiteSpace(game.VndbId) ? 7 : 0));
        return $"{question.Trim().ToLowerInvariant()}|{games.Count}|{maxAccessedTicks}|{totalPlaytimeTicks}|{metadataState}";
    }

    private static AiAssistantResponse CloneResponse(AiAssistantResponse source)
    {
        var clone = new AiAssistantResponse
        {
            Answer = source.Answer,
            SourceSummary = source.SourceSummary,
            DebugSummary = source.DebugSummary,
            ActionKind = source.ActionKind,
            ActionLabel = source.ActionLabel,
            UsedLocalTool = source.UsedLocalTool,
            UsedAi = source.UsedAi
        };

        foreach (var link in source.GameLinks)
        {
            clone.GameLinks.Add(new AiAssistantGameLink
            {
                GameId = link.GameId,
                Title = link.Title,
                Subtitle = link.Subtitle
            });
        }

        return clone;
    }

    private static async Task StreamContentAsync(AiAssistantMessage message, string content, CancellationToken cancellationToken)
    {
        const int chunkSize = 8;
        for (var i = 0; i < content.Length; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, content.Length - i);
            message.Content += content.Substring(i, length);
            await Task.Delay(12, cancellationToken);
        }
    }

    partial void OnInputTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanCancel));
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnBusyStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasBusyStatus));
    }

    private bool CanSendMessage()
    {
        return CanSend;
    }
}
