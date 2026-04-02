using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

/// <summary>
/// Converts a TimeSpan to a human-readable playtime string (e.g. "12h 30m").
/// </summary>
public class PlaytimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            int totalHours = (int)ts.TotalHours;
            int minutes = ts.Minutes;

            if (totalHours > 0)
                return $"{totalHours}h {minutes}m";
            if (minutes > 0)
                return $"{minutes}m";
            return "0m";
        }
        return "0m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
