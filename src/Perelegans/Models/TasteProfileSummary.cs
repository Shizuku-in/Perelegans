using System.Collections.Generic;

namespace Perelegans.Models;

public class TasteProfileSummary
{
    public int TotalLibraryGames { get; set; }
    public int EligibleLibraryGames { get; set; }
    public List<string> TopPositiveTags { get; set; } = [];
    public List<string> NegativeTags { get; set; } = [];
    public List<string> PreferredDevelopers { get; set; } = [];
    public double? PreferredReleaseYear { get; set; }

    public bool HasEnoughSourceGames => EligibleLibraryGames >= 3;
}
