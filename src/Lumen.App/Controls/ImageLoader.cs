using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lumen.App.Services;

namespace Lumen.App.Controls;

/// <summary>
/// Async image loading for virtualized lists: set <c>ImageLoader.Url</c> on an Image and
/// the source arrives off-thread from the cache. Recycled containers are handled via a
/// per-element stamp so stale loads never overwrite newer content.
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject element) => (string?)element.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject element, string? value) => element.SetValue(UrlProperty, value);

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(96));

    public static int GetDecodeWidth(DependencyObject element) => (int)element.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject element, int value) =>
        element.SetValue(DecodeWidthProperty, value);

    private static readonly DependencyProperty StampProperty = DependencyProperty.RegisterAttached(
        "Stamp", typeof(int), typeof(ImageLoader), new PropertyMetadata(0));

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image || DesignerProperties.GetIsInDesignMode(image))
        {
            return;
        }

        var stamp = (int)image.GetValue(StampProperty) + 1;
        image.SetValue(StampProperty, stamp);
        image.Source = null;

        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var decodeWidth = GetDecodeWidth(image);
        _ = LoadAsync(image, url, decodeWidth, stamp);
    }

    private static async Task LoadAsync(Image image, string url, int decodeWidth, int stamp)
    {
        try
        {
            var cache = App.GetService<ImageSourceCache>();
            var source = await cache.GetAsync(url, decodeWidth, CancellationToken.None);
            if ((int)image.GetValue(StampProperty) == stamp)
            {
                image.Source = source;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Image load failed for {Url}", url);
        }
    }
}
