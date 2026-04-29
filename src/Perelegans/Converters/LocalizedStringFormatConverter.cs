using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Perelegans.Converters;

public class LocalizedStringFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not string format || string.IsNullOrWhiteSpace(format))
        {
            return string.Empty;
        }

        var args = new object?[values.Length - 1];
        for (var i = 1; i < values.Length; i++)
        {
            args[i - 1] = values[i] == DependencyProperty.UnsetValue ? null : values[i];
        }

        try
        {
            return string.Format(culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
