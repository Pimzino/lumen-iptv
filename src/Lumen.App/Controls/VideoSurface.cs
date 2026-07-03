using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lumen.App.Services.Playback;

namespace Lumen.App.Controls;

/// <summary>
/// Placeholder that hosts the app's single shared VideoView. Preview pane, full player,
/// and mini player each declare one with a different <see cref="Kind"/>; the playback
/// service moves the view between them without recreating the player.
/// </summary>
public sealed class VideoSurface : Decorator
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(VideoSurfaceKind), typeof(VideoSurface),
        new PropertyMetadata(VideoSurfaceKind.Preview));

    public VideoSurfaceKind Kind
    {
        get => (VideoSurfaceKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public VideoSurface()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            App.GetService<IPlaybackService>().RegisterSurface(Kind, this);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            App.GetService<IPlaybackService>().UnregisterSurface(Kind, this);
        }
    }
}
