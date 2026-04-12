using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

public class BulkDeleteSelectionModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool isBulkDeleteMode && isBulkDeleteMode
            ? System.Windows.Controls.SelectionMode.Extended
            : System.Windows.Controls.SelectionMode.Single;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}