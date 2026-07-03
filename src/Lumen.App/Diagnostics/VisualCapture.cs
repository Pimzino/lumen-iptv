using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Renders WPF visuals to PNG files. Used by the hidden gallery's screenshot mode and by
/// development-time visual review; not part of the user-facing app.
/// </summary>
public static class VisualCapture
{
    /// <summary>
    /// Lays out a detached element at the given width (unbounded height) and saves it as PNG.
    /// </summary>
    public static void SaveDetached(FrameworkElement element, string path, double width)
    {
        var height = element.Height is > 0 and not double.NaN ? element.Height : double.PositiveInfinity;
        element.Measure(new Size(width, height));
        var arranged = double.IsInfinity(height) ? element.DesiredSize.Height : height;
        element.Arrange(new Rect(0, 0, width, arranged));
        element.UpdateLayout();
        SaveLive(element, path);
    }

    /// <summary>Renders a shown window — including its template chrome — to PNG.</summary>
    public static void SaveWindow(Window window, string path)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        SaveBitmap(bitmap, path);
    }

    /// <summary>
    /// Saves an already-laid-out element (e.g. a section of a larger tree) as PNG.
    /// <paramref name="background"/> is painted first so transparent sections keep the
    /// app's dark backdrop instead of washing out to white.
    /// </summary>
    public static void SaveLive(FrameworkElement element, string path, Brush? background = null)
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
        {
            throw new InvalidOperationException("Element has no renderable bounds; is it laid out?");
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var rect = new Rect(new Point(), bounds.Size);
            if (background is not null)
            {
                context.DrawRectangle(background, null, rect);
            }

            context.DrawRectangle(new VisualBrush(element), null, rect);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SaveBitmap(bitmap, path);
    }

    private static void SaveBitmap(RenderTargetBitmap bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
