using System;

namespace Perelegans.Models;

public class RecommendationFeedback
{
    public int Id { get; set; }
    public string VndbId { get; set; } = string.Empty;
    public double PositiveSignal { get; set; }
    public double NegativeSignal { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? LastPositiveAt { get; set; }
    public DateTime? LastNegativeAt { get; set; }
}
