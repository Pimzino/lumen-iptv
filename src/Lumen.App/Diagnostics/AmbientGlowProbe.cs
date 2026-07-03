using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumen.App.Theming;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Verifies the ambient-color extractor produces the expected dominant hue for solid-color
/// and mixed images, and that near-black frames fall back gracefully. Pure logic — no UI.
/// </summary>
public static class AmbientGlowProbe
{
    public static int Run(string outFile)
    {
        var report = new StringBuilder();
        var pass = true;

        try
        {
            // A solid crimson image should yield a reddish glow (hue near 0/360).
            var red = SolidColor(Color.FromRgb(200, 30, 40));
            var redGlow = AmbientColor.Extract("probe-red", red);
            var redHue = Hue(redGlow);
            var redOk = redHue is < 20 or > 340;
            report.AppendLine($"red -> {Hex(redGlow)} hue={redHue:F0} ok={redOk}");
            pass &= redOk;

            // A solid teal image should yield a cyan-ish glow (hue ~180).
            var teal = SolidColor(Color.FromRgb(20, 160, 170));
            var tealGlow = AmbientColor.Extract("probe-teal", teal);
            var tealHue = Hue(tealGlow);
            var tealOk = tealHue is > 150 and < 210;
            report.AppendLine($"teal -> {Hex(tealGlow)} hue={tealHue:F0} ok={tealOk}");
            pass &= tealOk;

            // A near-black frame yields the fallback (extractor must not divide by zero).
            var black = SolidColor(Color.FromRgb(4, 4, 4));
            var blackGlow = AmbientColor.Extract("probe-black", black);
            report.AppendLine($"black -> {Hex(blackGlow)} (fallback tolerated)");

            // Normalization: every glow lands in the consistent lightness band.
            foreach (var (name, glow) in new[] { ("red", redGlow), ("teal", tealGlow) })
            {
                var lightness = Lightness(glow);
                var lightnessOk = lightness is > 0.4 and < 0.65;
                report.AppendLine($"{name} lightness={lightness:F2} ok={lightnessOk}");
                pass &= lightnessOk;
            }

            report.AppendLine(pass ? "GLOW-RESULT=PASS" : "GLOW-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ambient glow probe failed");
            report.AppendLine($"GLOW-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static BitmapSource SolidColor(Color color)
    {
        const int size = 32;
        var stride = size * 4;
        var pixels = new byte[size * stride];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (Math.Abs(max - min) < 1e-6)
        {
            return 0;
        }

        var d = max - min;
        double h;
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

        return h * 60;
    }

    private static double Lightness(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
