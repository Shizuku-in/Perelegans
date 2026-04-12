using System;
using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Perelegans.Converters;

public class CoverArtImageSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        var trimmedUrl = url.Trim();
        if (Cache.TryGetValue(trimmedUrl, out var cachedImage))
            return cachedImage;

        var imageSource = CreateImageSource(trimmedUrl);
        if (imageSource != null)
        {
            Cache[trimmedUrl] = imageSource;
        }

        return imageSource;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static ImageSource? CreateImageSource(string url)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = File.Exists(url) ? new Uri(Path.GetFullPath(url), UriKind.Absolute) : new Uri(url, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 1200;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}