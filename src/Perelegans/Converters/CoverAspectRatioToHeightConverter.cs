using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

public class CoverAspectRatioToHeightConverter : IMultiValueConverter
{
    private const double DefaultCardWidth = 224d;
    private const double DefaultAspectRatio = 0.68d;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double actualWidth && actualWidth > 0
            ? actualWidth
            : DefaultCardWidth;

        var ratio = values.Length > 1 && values[1] is double actualRatio && actualRatio > 0
            ? actualRatio
            : DefaultAspectRatio;

        var computedHeight = width / ratio;
        return double.IsFinite(computedHeight) && computedHeight > 0
            ? computedHeight
            : width / DefaultAspectRatio;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
