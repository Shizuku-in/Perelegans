using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

public class CoverAspectRatioToHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double actualWidth && actualWidth > 0
            ? actualWidth
            : 210d;

        var ratio = values.Length > 1 && values[1] is double actualRatio && actualRatio > 0
            ? actualRatio
            : 0.68d;

        var computedHeight = width / ratio;
        return Math.Clamp(computedHeight, 160d, 360d);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
