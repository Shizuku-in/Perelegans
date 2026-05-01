using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

public class AssistantMessageAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
