using System.Collections.Generic;

namespace Perelegans.Models;

public class AiRecommendationExplanation
{
    public string CandidateId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<string> MatchingTags { get; set; } = [];
    public string Caution { get; set; } = string.Empty;
    public string SellingPoint { get; set; } = string.Empty;
}

public class AiRecommendationResult
{
    public List<AiRecommendationExplanation> Explanations { get; set; } = [];
    public string ErrorMessage { get; set; } = string.Empty;

    public bool HasExplanations => Explanations.Count > 0;
}
