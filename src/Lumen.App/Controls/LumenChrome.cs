using System.Windows;
using System.Windows.Input;

namespace Lumen.App.Controls;

/// <summary>
/// Window-chrome plumbing for the custom title bar: binds the caption buttons'
/// SystemCommands and exposes a slot for title-bar content (search box, profile chip).
/// </summary>
public static class LumenChrome
{
    /// <summary>Content hosted in the center of the custom title bar.</summary>
    public static readonly DependencyProperty TitleBarContentProperty = DependencyProperty.RegisterAttached(
        "TitleBarContent", typeof(object), typeof(LumenChrome), new PropertyMetadata(null));

    public static object? GetTitleBarContent(DependencyObject element) => element.GetValue(TitleBarContentProperty);

    public static void SetTitleBarContent(DependencyObject element, object? value) =>
        element.SetValue(TitleBarContentProperty, value);

    /// <summary>
    /// Immersive mode hides the title bar and caption buttons (edge-to-edge player).
    /// The window template collapses its chrome when this is set.
    /// </summary>
    public static readonly DependencyProperty IsImmersiveProperty = DependencyProperty.RegisterAttached(
        "IsImmersive", typeof(bool), typeof(LumenChrome), new PropertyMetadata(false, OnIsImmersiveChanged));

    public static bool GetIsImmersive(DependencyObject element) => (bool)element.GetValue(IsImmersiveProperty);

    public static void SetIsImmersive(DependencyObject element, bool value) =>
        element.SetValue(IsImmersiveProperty, value);

    /// <summary>Caption drag-zone height outside immersive mode. Matches Lumen.Size.TitleBar (48).</summary>
    private const double CaptionHeight = 48;

    private static void OnIsImmersiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
        {
            return;
        }

        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        if (chrome is null)
        {
            return;
        }

        // The chrome applied through the window Style is a shared, frozen Freezable, so its
        // properties can't be mutated in place (doing so throws "read-only state"). Clone it to a
        // modifiable copy, drop the caption drag zone to 0 while immersive — so the video's top
        // edge stays interactive — and re-apply. Both the enter (True) and exit (False) transitions
        // run through here, since the Style trigger reverts the attached property on deactivation.
        var caption = e.NewValue is true ? 0d : CaptionHeight;
        if (Math.Abs(chrome.CaptionHeight - caption) < 0.01)
        {
            return;
        }

        var updated = (System.Windows.Shell.WindowChrome)chrome.CloneCurrentValue();
        updated.CaptionHeight = caption;
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, updated);
    }

    /// <summary>Adds CommandBindings for minimize/maximize/restore/close on the window.</summary>
    public static readonly DependencyProperty EnableWindowCommandsProperty = DependencyProperty.RegisterAttached(
        "EnableWindowCommands", typeof(bool), typeof(LumenChrome), new PropertyMetadata(false, OnEnableWindowCommandsChanged));

    public static bool GetEnableWindowCommands(DependencyObject element) =>
        (bool)element.GetValue(EnableWindowCommandsProperty);

    public static void SetEnableWindowCommands(DependencyObject element, bool value) =>
        element.SetValue(EnableWindowCommandsProperty, value);

    private static void OnEnableWindowCommandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand,
            (_, _) => SystemCommands.CloseWindow(window)));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(window)));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (_, _) =>
            {
                if (window.WindowState == WindowState.Maximized)
                {
                    SystemCommands.RestoreWindow(window);
                }
                else
                {
                    SystemCommands.MaximizeWindow(window);
                }
            }));
    }
}
