using System;

namespace Perelegans.Models;

public sealed class BangumiAccount
{
    public long Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;

    public string DisplayName => !string.IsNullOrWhiteSpace(Nickname) ? Nickname : Username;
}

public sealed class BangumiCollectionState
{
    public int SubjectId { get; init; }
    public int Type { get; init; }
    public int? Rating { get; init; }
    public string? Comment { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
