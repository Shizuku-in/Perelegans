using System;
using System.Globalization;
using System.Windows.Data;
using MahApps.Metro.IconPacks;
using Perelegans.Models;

namespace Perelegans.Converters;

/// <summary>
/// Converts GameStatus enum to a PackIconMaterialKind for display.
/// </summary>
public class StatusIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is GameStatus status)
        {
            return status switch
            {
                GameStatus.Playing => PackIconMaterialKind.PlayCircleOutline,
                GameStatus.Completed => PackIconMaterialKind.CheckCircleOutline,
                GameStatus.Dropped => PackIconMaterialKind.CloseCircleOutline,
                GameStatus.Planned => PackIconMaterialKind.ClockOutline,
                _ => PackIconMaterialKind.HelpCircleOutline
            };
        }
        return PackIconMaterialKind.HelpCircleOutline;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
