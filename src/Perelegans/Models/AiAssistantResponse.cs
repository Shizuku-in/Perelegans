using System.Collections.Generic;

namespace Perelegans.Models;

public class AiAssistantResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<AiAssistantGameLink> GameLinks { get; } = new();
    public string SourceSummary { get; set; } = string.Empty;
    public string DebugSummary { get; set; } = string.Empty;
    public AiAssistantActionKind ActionKind { get; set; } = AiAssistantActionKind.None;
    public string ActionLabel { get; set; } = string.Empty;
    public bool UsedLocalTool { get; set; }
    public bool UsedAi { get; set; }
}
