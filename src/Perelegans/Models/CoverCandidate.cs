namespace Perelegans.Models;

public sealed class CoverCandidate
{
    public string Source { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public DateTime? ReleaseDate { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public string PreviewSource { get; set; } = string.Empty;
    public string? WebUrl { get; init; }

    public static CoverCandidate FromMetadataResult(MetadataResult result)
    {
        return new CoverCandidate
        {
            Source = result.Source,
            SourceId = result.SourceId,
            Title = result.Title,
            Brand = result.Brand,
            ReleaseDate = result.ReleaseDate,
            ImageUrl = result.ImageUrl ?? string.Empty,
            PreviewSource = result.ImageUrl ?? string.Empty,
            WebUrl = result.WebUrl
        };
    }
}
