using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Perelegans.Converters;

public class CoverAspectRatioToVerticalAlignmentConverter : IValueConverter
{
    private const double LandscapeThreshold = 1d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double aspectRatio && aspectRatio > LandscapeThreshold)
            return VerticalAlignment.Top;

        return VerticalAlignment.Center;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
