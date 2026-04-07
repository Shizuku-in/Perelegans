using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Perelegans.Services;

public static class TagUtilities
{
    public static List<string> Normalize(IEnumerable<string?>? tags)
    {
        var normalized = new List<string>();
        if (tags == null)
            return normalized;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var trimmed = tag.Trim();
            if (seen.Add(trimmed))
                normalized.Add(trimmed);
        }

        return normalized;
    }

    public static List<string> Merge(IEnumerable<string?>? existingTags, IEnumerable<string?>? newTags)
    {
        var merged = Normalize(existingTags);
        if (newTags == null)
            return merged;

        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in newTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var trimmed = tag.Trim();
            if (seen.Add(trimmed))
                merged.Add(trimmed);
        }

        return merged;
    }

    public static List<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(json);
            return Normalize(tags);
        }
        catch (JsonException)
        {
            return ParseMultilineText(json);
        }
    }

    public static string? Serialize(IEnumerable<string?>? tags)
    {
        var normalized = Normalize(tags);
        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    public static List<string> ParseMultilineText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace('\r', '\n');
        return Normalize(normalizedText.Split('\n', StringSplitOptions.None));
    }

    public static string ToMultilineText(IEnumerable<string?>? tags)
    {
        return string.Join(Environment.NewLine, Normalize(tags));
    }
}
