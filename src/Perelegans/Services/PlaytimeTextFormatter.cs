using System;

namespace Perelegans.Services;

public static class PlaytimeTextFormatter
{
    public static string Format(TimeSpan playtime)
    {
        var translation = TranslationService.Instance;
        var totalHours = (int)playtime.TotalHours;
        var minutes = playtime.Minutes;

        if (totalHours > 0)
        {
            return string.Format(
                translation.CurrentCulture,
                translation["Playtime_HoursMinutes"],
                totalHours,
                minutes);
        }

        if (minutes > 0)
        {
            return string.Format(
                translation.CurrentCulture,
                translation["Playtime_Minutes"],
                minutes);
        }

        return translation["Playtime_Zero"];
    }
}
