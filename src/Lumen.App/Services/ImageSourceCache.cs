using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumen.Core.Abstractions;

namespace Lumen.App.Services;

/// <summary>
/// Turns image URLs into frozen, decode-sized ImageSources backed by the disk cache,
/// with a bounded in-memory LRU so virtualized lists never re-decode while scrolling.
/// </summary>
public sealed class ImageSourceCache
{
    private const int Capacity = 400;

    private readonly IImageCache _diskCache;
    private readonly object _gate = new();
    private readonly Dictionary<(string Url, int Width), LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _recency = [];

    private sealed record CacheEntry((string Url, int Width) Key, ImageSource? Source);

    public ImageSourceCache(IImageCache diskCache)
    {
        _diskCache = diskCache;
    }

    /// <summary>Null when the image cannot be fetched — callers fall back to a monogram.</summary>
    public async Task<ImageSource?> GetAsync(string? url, int decodeWidth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var key = (url, decodeWidth);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Source;
            }
        }

        var path = await _diskCache.GetLocalPathAsync(url, cancellationToken);
        if (path is null)
        {
            return null;
        }

        var source = await Task.Run(() => Decode(path, decodeWidth), cancellationToken);
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                var node = _recency.AddFirst(new CacheEntry(key, source));
                _entries[key] = node;
                while (_entries.Count > Capacity)
                {
                    var oldest = _recency.Last!;
                    _recency.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                }
            }
        }

        return source;
    }

    /// <summary>
    /// Returns the signature "ambient" color for an image URL, computed from the cached
    /// bitmap. Falls back to the accent color when the image can't be decoded.
    /// </summary>
    public async Task<System.Windows.Media.Color> GetAmbientColorAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Theming.AmbientColor.Fallback;
        }

        if (Theming.AmbientColor.TryGet(url) is { } cached)
        {
            return cached;
        }

        var path = await _diskCache.GetLocalPathAsync(url, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return Theming.AmbientColor.Fallback;
        }

        return await Task.Run(() =>
        {
            if (Decode(path, 32) is BitmapSource bitmap)
            {
                return Theming.AmbientColor.Extract(url, bitmap);
            }

            return Theming.AmbientColor.Fallback;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ImageSource? Decode(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or ArgumentException)
        {
            return null; // corrupt/unsupported image — monogram fallback
        }
    }
}
