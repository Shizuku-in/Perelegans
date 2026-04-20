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

    public static void InvalidateCache(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        Cache.TryRemove(source.Trim(), out _);
    }

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
            if (!TryResolveSourceUri(url, out var sourceUri, out _))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = sourceUri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.DelayCreation;
            image.DecodePixelWidth = 320;
            image.EndInit();

            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveSourceUri(string source, out Uri uri, out bool isLocalSource)
    {
        isLocalSource = false;

        if (File.Exists(source))
        {
            uri = new Uri(Path.GetFullPath(source), UriKind.Absolute);
            isLocalSource = true;
            return true;
        }

        var normalized = source.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{source}"
            : source;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri!))
            return false;

        isLocalSource = uri.IsFile;
        return true;
    }
}
