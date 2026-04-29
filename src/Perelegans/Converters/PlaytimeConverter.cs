using System;
using System.Globalization;
using System.Windows.Data;
using Perelegans.Services;

namespace Perelegans.Converters;

/// <summary>
/// Converts a TimeSpan to a localized human-readable playtime string.
/// </summary>
public class PlaytimeConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TimeSpan ts
            ? PlaytimeTextFormatter.Format(ts)
            : PlaytimeTextFormatter.Format(TimeSpan.Zero);
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length > 0 && values[0] is TimeSpan ts
            ? PlaytimeTextFormatter.Format(ts)
            : PlaytimeTextFormatter.Format(TimeSpan.Zero);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
