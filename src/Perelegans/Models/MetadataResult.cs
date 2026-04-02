using System;

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
    public string Brand { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public string? ImageUrl { get; set; }
    public string? WebUrl { get; set; }
}
