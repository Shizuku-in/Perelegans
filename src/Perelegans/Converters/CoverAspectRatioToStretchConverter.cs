using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Perelegans.Converters;

public class CoverAspectRatioToStretchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double ratio && ratio > 0)
        {
            return ratio >= 1.18 ? Stretch.Uniform : Stretch.UniformToFill;
        }

        return Stretch.UniformToFill;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}