using System.IO;
using System.Windows;
using Lumen.App.Diagnostics;

namespace Lumen.App.Views.Debug;

/// <summary>Hidden debug window hosting the design-system gallery (--gallery).</summary>
public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
    }

    /// <summary>Captures each gallery section plus the full page as PNGs.</summary>
    public void CaptureSections(string directory)
    {
        Directory.CreateDirectory(directory);
        var backdrop = (System.Windows.Media.Brush)FindResource("Lumen.Brush.Bg.Base");
        foreach (var (name, element) in Gallery.Sections)
        {
            VisualCapture.SaveLive(element, Path.Combine(directory, $"{name}.png"), backdrop);
        }

        VisualCapture.SaveLive(Gallery, Path.Combine(directory, "gallery-full.png"), backdrop);
        VisualCapture.SaveWindow(this, Path.Combine(directory, "window-chrome.png"));
    }
}
