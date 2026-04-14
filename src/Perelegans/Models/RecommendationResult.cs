using System.Collections.Generic;

namespace Perelegans.Models;

public class RecommendationResult
{
    public TasteProfileSummary ProfileSummary { get; set; } = new();
    public List<RecommendationCandidate> Candidates { get; set; } = [];
}
