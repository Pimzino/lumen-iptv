using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumen.App.Theming;

/// <summary>
/// Extracts a single representative "ambient" color from an image — the seed for Lumen's
/// signature glow. The image is downscaled to a tiny thumbnail, saturated pixels are averaged
/// (so the wash takes the poster's mood, not its black bars), and the result is nudged toward a
/// consistent lightness so every glow reads the same weight.
/// </summary>
public static class AmbientColor
{
    private const int SampleSize = 16;

    private static readonly ConcurrentDictionary<string, Color> Cache = new(StringComparer.Ordinal);

    /// <summary>The neutral fallback glow when no image is available.</summary>
    public static Color Fallback { get; } = Color.FromRgb(0x4C, 0x8D, 0xFF);

    /// <summary>Returns a cached color for a key if one was previously computed.</summary>
    public static Color? TryGet(string key) => Cache.TryGetValue(key, out var color) ? color : null;

    /// <summary>Extracts (and caches) the ambient color from a decoded bitmap.</summary>
    public static Color Extract(string key, BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var color = Compute(source);
        Cache[key] = color;
        return color;
    }

    private static Color Compute(BitmapSource source)
    {
        try
        {
            var scaled = new TransformedBitmap(
                source, new ScaleTransform(
                    SampleSize / (double)source.PixelWidth, SampleSize / (double)source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            double sumR = 0, sumG = 0, sumB = 0, weightTotal = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i];
                double g = pixels[i + 1];
                double r = pixels[i + 2];
                double a = pixels[i + 3];
                if (a < 32)
                {
                    continue;
                }

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                // Weight by saturation + a floor so near-greys still contribute a little.
                var saturation = max <= 0 ? 0 : (max - min) / max;
                var weight = 0.15 + saturation;
                sumR += r * weight;
                sumG += g * weight;
                sumB += b * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0)
            {
                return Fallback;
            }

            var avg = Color.FromRgb(
                (byte)(sumR / weightTotal), (byte)(sumG / weightTotal), (byte)(sumB / weightTotal));
            return Normalize(avg);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or OverflowException)
        {
            return Fallback;
        }
    }

    /// <summary>Pushes the color to a consistent mid lightness and modest saturation.</summary>
    private static Color Normalize(Color color)
    {
        RgbToHsl(color, out var h, out var s, out var l);
        s = Math.Clamp(s, 0.35, 0.7);
        l = Math.Clamp(l, 0.45, 0.6);
        return HslToRgb(h, s, l);
    }

    private static void RgbToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-6)
        {
            h = s = 0;
            return;
        }

        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (Math.Abs(max - r) < 1e-6)
        {
            h = (g - b) / d + (g < b ? 6 : 0);
        }
        else if (Math.Abs(max - g) < 1e-6)
        {
            h = (b - r) / d + 2;
        }
        else
        {
            h = (r - g) / d + 4;
        }

        h /= 6;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s < 1e-6)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + (q - p) * (2.0 / 3 - t) * 6;
        }

        return p;
    }
}
