using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lumen.App.Services;
using Lumen.App.Theming;

namespace Lumen.App.Controls;

/// <summary>
/// Lumen's signature effect. Set <c>AmbientGlow.SourceUrl</c> on a Border and its background
/// becomes a soft radial wash of the image's sampled dominant color at ~12% opacity — the
/// content's mood bleeding into the surface. Used on the now-playing bar and zap banner.
/// </summary>
public static class AmbientGlow
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.RegisterAttached(
        "SourceUrl", typeof(string), typeof(AmbientGlow), new PropertyMetadata(null, OnSourceUrlChanged));

    public static string? GetSourceUrl(DependencyObject element) => (string?)element.GetValue(SourceUrlProperty);

    public static void SetSourceUrl(DependencyObject element, string? value) =>
        element.SetValue(SourceUrlProperty, value);

    /// <summary>Peak opacity of the wash. Spec calls for ~12%.</summary>
    public static readonly DependencyProperty IntensityProperty = DependencyProperty.RegisterAttached(
        "Intensity", typeof(double), typeof(AmbientGlow), new PropertyMetadata(0.12));

    public static double GetIntensity(DependencyObject element) => (double)element.GetValue(IntensityProperty);

    public static void SetIntensity(DependencyObject element, double value) =>
        element.SetValue(IntensityProperty, value);

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border || DesignerProperties.GetIsInDesignMode(border))
        {
            return;
        }

        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            ApplyColor(border, AmbientColor.Fallback);
            return;
        }

        // Instant if the color is already cached; otherwise resolve off-thread and fade in.
        if (AmbientColor.TryGet(url) is { } cached)
        {
            ApplyColor(border, cached);
        }
        else
        {
            _ = ResolveAsync(border, url);
        }
    }

    private static async Task ResolveAsync(Border border, string url)
    {
        try
        {
            var color = await App.GetService<ImageSourceCache>().GetAmbientColorAsync(url, CancellationToken.None);

            // The container may have been recycled onto a different URL while we resolved.
            if (string.Equals(GetSourceUrl(border), url, StringComparison.Ordinal))
            {
                ApplyColor(border, color, animate: true);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Ambient glow resolve failed for {Url}", url);
        }
    }

    private static void ApplyColor(Border border, Color color, bool animate = false)
    {
        var intensity = GetIntensity(border);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.35),
            Center = new Point(0.5, 0.35),
            RadiusX = 0.9,
            RadiusY = 1.1,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * intensity), color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();

        border.Background = brush;
        if (animate && MotionSettings.AnimationsEnabled)
        {
            border.BeginAnimation(UIElement.OpacityProperty, null);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            border.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
