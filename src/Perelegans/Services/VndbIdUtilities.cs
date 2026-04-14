using System;

namespace Perelegans.Services;

public static class VndbIdUtilities
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;

        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[1..];
            return suffix.Length == 0 ? null : $"v{suffix}";
        }

        return $"v{trimmed}";
    }

    public static string? ToWebUrl(string? value)
    {
        var normalized = Normalize(value);
        return normalized == null ? null : $"https://vndb.org/{normalized}";
    }
}
