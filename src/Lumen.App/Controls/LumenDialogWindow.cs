using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace Lumen.App.Controls;

/// <summary>
/// Base window for Lumen dialogs: borderless, transparent (for the soft drop shadow),
/// draggable by its surface, styled by <c>Lumen.DialogWindow</c>.
/// </summary>
public class LumenDialogWindow : Window
{
    public LumenDialogWindow()
    {
        // These must be set from code before the window is shown; style setter order
        // is not guaranteed for the WindowStyle/AllowsTransparency pair.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        SetResourceReference(StyleProperty, "Lumen.DialogWindow");
    }
}
