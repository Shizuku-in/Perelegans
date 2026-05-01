using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class AiAssistantViewModel : ObservableObject
{
    private readonly AiLibraryAssistantService _assistantService;
    private readonly IReadOnlyCollection<Game> _games;

    [ObservableProperty]
    private ObservableCollection<AiAssistantMessage> _messages = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyStatusText = string.Empty;

    public bool HasBusyStatus => !string.IsNullOrWhiteSpace(BusyStatusText);

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    public AiAssistantViewModel(HttpClient httpClient, SettingsService settingsService, IReadOnlyCollection<Game> games)
    {
        _assistantService = new AiLibraryAssistantService(httpClient, settingsService);
        _games = games;
        Messages.Add(new AiAssistantMessage
        {
            Role = "assistant",
            Content = TranslationService.Instance["Assistant_Welcome"]
        });
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task Send()
    {
        var question = InputText.Trim();
        if (string.IsNullOrWhiteSpace(question) || IsBusy)
            return;

        InputText = string.Empty;
        Messages.Add(new AiAssistantMessage { Role = "user", Content = question });

        IsBusy = true;
        BusyStatusText = TranslationService.Instance["Assistant_SearchingLibrary"];
        try
        {
            await Task.Delay(150);
            BusyStatusText = TranslationService.Instance["Assistant_Thinking"];
            var answer = await _assistantService.AskAsync(question, _games);
            Messages.Add(new AiAssistantMessage
            {
                Role = "assistant",
                Content = string.IsNullOrWhiteSpace(answer)
                    ? TranslationService.Instance["Assistant_NoAnswer"]
                    : answer
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
            BusyStatusText = string.Empty;
            IsBusy = false;
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
        SendCommand.NotifyCanExecuteChanged();
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
