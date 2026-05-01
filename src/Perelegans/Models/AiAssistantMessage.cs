using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Perelegans.Models;

public class AiAssistantMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;

    public string Role { get; set; } = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value)
                return;

            _content = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasContent));
        }
    }

    public ObservableCollection<AiAssistantGameLink> GameLinks { get; } = new();
    public string SourceSummary { get; set; } = string.Empty;
    public string DebugSummary { get; set; } = string.Empty;
    public AiAssistantActionKind ActionKind { get; set; } = AiAssistantActionKind.None;
    public string ActionLabel { get; set; } = string.Empty;
    public bool IsUser => Role == "user";
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
    public bool HasGameLinks => GameLinks.Count > 0;
    public bool HasSourceSummary => !string.IsNullOrWhiteSpace(SourceSummary);
    public bool HasDebugSummary => !string.IsNullOrWhiteSpace(DebugSummary);
    public bool HasAction => ActionKind != AiAssistantActionKind.None && GameLinks.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AiAssistantGameLink
{
    public int GameId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
}

public enum AiAssistantActionKind
{
    None,
    FilterGames,
    DraftBangumiLookup
}
