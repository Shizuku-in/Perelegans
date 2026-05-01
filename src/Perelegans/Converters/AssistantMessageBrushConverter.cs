using System;
using System.Globalization;
using System.Windows.Data;

namespace Perelegans.Converters;

public class AssistantMessageBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isUser = value is true;
        var key = isUser ? "Perelegans.AccentBrush" : "Perelegans.CardBackgroundBrush";
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
