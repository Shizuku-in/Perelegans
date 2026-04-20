using System.Collections.Generic;

namespace Perelegans.Models;

public class TasteProfileSummary
{
    public int TotalLibraryGames { get; set; }
    public int EligibleLibraryGames { get; set; }
    public int CompletedGames { get; set; }
    public int DroppedGames { get; set; }
    public List<string> TopPositiveTags { get; set; } = [];
    public List<string> SecondaryPositiveTags { get; set; } = [];
    public List<string> NegativeTags { get; set; } = [];
    public List<string> SoftNegativeTags { get; set; } = [];
    public List<string> PreferredDevelopers { get; set; } = [];
    public double? PreferredReleaseYear { get; set; }
    public double CompletionRate { get; set; }
    public double AveragePlaytimeHours { get; set; }
    public double AverageCompletedHours { get; set; }
    public double AverageDroppedHours { get; set; }
    public string PreferenceStyle { get; set; } = string.Empty;

    public bool HasEnoughSourceGames => EligibleLibraryGames >= 3;
}
