using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lumen.App.Controls;

/// <summary>
/// Windows 11 window effects for the custom chrome: immersive dark frame, rounded corners,
/// Mica system backdrop, and Snap Layouts on the custom maximize button.
///
/// The DWM must own non-client painting (the window styles keep a glass frame) — a zero glass
/// frame hands NC repaints to the classic theme, which is what used to draw Windows-95-style
/// caption buttons over the custom title bar whenever ResizeMode/WindowChrome changed at runtime.
/// Everything here degrades gracefully: on Windows 10 the window simply stays a solid dark
/// rectangle with square corners.
/// </summary>
public static class WindowFx
{
    /// <summary>
    /// Diagnostics (screenshot gates, benches) disable the translucent backdrop so captures
    /// stay deterministic; RenderTargetBitmap cannot see the DWM backdrop anyway and would
    /// bake the tint's alpha into the PNG.
    /// </summary>
    internal static bool DisableBackdropForDiagnostics { get; set; }

    private static readonly ConditionalWeakTable<Window, State> States = [];

    private sealed class State
    {
        public Button? MaximizeButton;
        public bool NcPressOnMaxButton;
    }

    // ======================= Effects attached property =======================

    /// <summary>
    /// Set on a Window (in style or XAML) to apply the DWM effects when its handle exists.
    /// "Mica" also requests the system backdrop; "Frame" applies dark mode + rounded corners only.
    /// </summary>
    public static readonly DependencyProperty EffectsProperty = DependencyProperty.RegisterAttached(
        "Effects", typeof(string), typeof(WindowFx), new PropertyMetadata(null, OnEffectsChanged));

    public static string? GetEffects(DependencyObject element) => (string?)element.GetValue(EffectsProperty);

    public static void SetEffects(DependencyObject element, string? value) => element.SetValue(EffectsProperty, value);

    /// <summary>
    /// True once a real system backdrop (Mica/Acrylic) is active behind the window. Window
    /// templates trigger on this to swap opaque backgrounds for translucent tints.
    /// </summary>
    private static readonly DependencyPropertyKey IsBackdropActivePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsBackdropActive", typeof(bool), typeof(WindowFx), new PropertyMetadata(false));

    public static readonly DependencyProperty IsBackdropActiveProperty = IsBackdropActivePropertyKey.DependencyProperty;

    public static bool GetIsBackdropActive(DependencyObject element) => (bool)element.GetValue(IsBackdropActiveProperty);

    private static void OnEffectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not string mode || string.IsNullOrEmpty(mode))
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window, mode);
        }
        else
        {
            window.SourceInitialized += OnSourceInitialized;
        }

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Apply(window, mode);
        }
    }

    private static void Apply(Window window, string mode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ApplyDarkFrame(hwnd);
        ApplyRoundedCorners(hwnd);

        if (!string.Equals(mode, "Mica", StringComparison.OrdinalIgnoreCase) || DisableBackdropForDiagnostics)
        {
            return;
        }

        if (TryApplyBackdrop(hwnd))
        {
            // The backdrop only shows where nothing opaque is drawn: clear WPF's composition
            // background and let the template's translucent tint reveal it.
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            window.SetValue(IsBackdropActivePropertyKey, true);
        }

        // HwndSource invokes hooks newest-first and stops at the first handled message, so the
        // Snap Layouts hook must be registered AFTER WindowChromeWorker's own NCHITTEST hook
        // (which answers HTCLIENT for IsHitTestVisibleInChrome elements and would otherwise
        // swallow the maximize button's rect). Deferring to Loaded priority guarantees it.
        window.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () => InstallSnapLayoutsHook(window, hwnd));
    }

    /// <summary>Dark non-client frame (title-bar remnants, top hairline, transition flashes).</summary>
    private static void ApplyDarkFrame(IntPtr hwnd)
    {
        var enabled = 1;
        // 20 on 20H1+; 19 on 1809–1909.
        if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
    }

    /// <summary>Windows 11 rounded window corners (no-op on Windows 10).</summary>
    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    private static bool TryApplyBackdrop(IntPtr hwnd)
    {
        // 22H2's system backdrop paints the whole window's base material without extending
        // the DWM frame sheet. Deliberately no 21H2 (DWMWA_MICA_EFFECT 1029) fallback: that
        // legacy mechanism only rendered where the frame was extended, and a full-window
        // extension makes the DWM paint ghost caption buttons under the custom title bar.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        var mica = 2; // DWMSBT_MAINWINDOW
        return DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int)) == 0; // DWMWA_SYSTEMBACKDROP_TYPE
    }

    // ======================= Snap Layouts (custom maximize button) =======================

    /// <summary>
    /// Marks the template's maximize/restore button so the Win11 Snap Layouts flyout appears on
    /// hover. The window's WndProc answers HTMAXBUTTON for the button's bounds; because that
    /// steals WPF mouse input, hover/press visuals are driven via the attached properties below.
    /// </summary>
    public static readonly DependencyProperty IsSnapTargetProperty = DependencyProperty.RegisterAttached(
        "IsSnapTarget", typeof(bool), typeof(WindowFx), new PropertyMetadata(false, OnIsSnapTargetChanged));

    public static bool GetIsSnapTarget(DependencyObject element) => (bool)element.GetValue(IsSnapTargetProperty);

    public static void SetIsSnapTarget(DependencyObject element, bool value) => element.SetValue(IsSnapTargetProperty, value);

    /// <summary>Hover visual driven from WM_NCHITTEST (WPF never sees NC mouse moves).</summary>
    public static readonly DependencyProperty IsNcHoverProperty = DependencyProperty.RegisterAttached(
        "IsNcHover", typeof(bool), typeof(WindowFx), new PropertyMetadata(false));

    public static bool GetIsNcHover(DependencyObject element) => (bool)element.GetValue(IsNcHoverProperty);

    public static void SetIsNcHover(DependencyObject element, bool value) => element.SetValue(IsNcHoverProperty, value);

    private static void OnIsSnapTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button)
        {
            return;
        }

        if (button.IsLoaded)
        {
            Register(button);
        }
        else
        {
            button.Loaded += OnLoaded;
        }

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            button.Loaded -= OnLoaded;
            Register(button);
        }

        static void Register(Button button)
        {
            if (Window.GetWindow(button) is { } window)
            {
                States.GetOrCreateValue(window).MaximizeButton = button;
            }
        }
    }

    private static void InstallSnapLayoutsHook(Window window, IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd) is not { } source)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);

        // Added after WindowChrome's own hook (hooks run newest-first), so HTMAXBUTTON wins
        // for the button rect and everything else falls through untouched.
        source.AddHook(Hook);

        IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int WmNcMouseLeave = 0x02A2;
            const int WmNcLButtonDown = 0x00A1;
            const int WmNcLButtonUp = 0x00A2;
            const int HtMaxButton = 9;

            switch (msg)
            {
                case WmNcHitTest:
                {
                    var over = IsOverMaximizeButton(state, lParam);
                    SetHover(state, over);
                    if (over)
                    {
                        handled = true;
                        return HtMaxButton;
                    }

                    break;
                }

                case WmNcMouseLeave:
                    SetHover(state, false);
                    state.NcPressOnMaxButton = false;
                    break;

                case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                    state.NcPressOnMaxButton = true;
                    handled = true;
                    break;

                case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                    if (state.NcPressOnMaxButton)
                    {
                        state.NcPressOnMaxButton = false;
                        if (window.WindowState == WindowState.Maximized)
                        {
                            SystemCommands.RestoreWindow(window);
                        }
                        else
                        {
                            SystemCommands.MaximizeWindow(window);
                        }
                    }

                    handled = true;
                    break;

                default:
                    break;
            }

            return IntPtr.Zero;
        }
    }

    private static void SetHover(State state, bool value)
    {
        if (state.MaximizeButton is { } button && GetIsNcHover(button) != value)
        {
            SetIsNcHover(button, value);
        }
    }

    private static bool IsOverMaximizeButton(State state, IntPtr lParam)
    {
        if (state.MaximizeButton is not { IsVisible: true } button)
        {
            return false;
        }

        try
        {
            // Signed 16-bit screen coordinates (can be negative on multi-monitor setups).
            var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var point = button.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.X < button.ActualWidth &&
                   point.Y >= 0 && point.Y < button.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false; // button detached from a presentation source mid-move
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
