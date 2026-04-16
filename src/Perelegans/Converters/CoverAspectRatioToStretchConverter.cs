using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Perelegans.Converters;

public class CoverAspectRatioToStretchConverter : IValueConverter
{
    private const double LandscapeThreshold = 1d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double aspectRatio && aspectRatio > LandscapeThreshold)
            return Stretch.Uniform;

        return Stretch.UniformToFill;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
