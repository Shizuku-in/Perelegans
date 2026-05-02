using System;
using System.Collections.Generic;

namespace Perelegans.Models;

/// <summary>
/// Unified metadata result from any source (VNDB, Bangumi, ErogameSpace).
/// </summary>
public class MetadataResult
{
    public string Source { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string ChineseTitle { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public string? ImageUrl { get; set; }
    public string? WebUrl { get; set; }
    public double? Rating { get; set; }
    public int? Rank { get; set; }
    public int? VoteCount { get; set; }
    public int? LengthMinutes { get; set; }
    public int? LengthVotes { get; set; }
    public int? LengthCategory { get; set; }
    public List<string> Tags { get; set; } = [];
}
