using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.App.Services.Playback;
using Lumen.App.ViewModels;

namespace Lumen.App.Controls;

/// <summary>
/// Borderless, always-on-top (pinnable) floating picture-in-picture window hosting the shared video
/// surface. Controls live in a <c>Popup</c> — the only WPF surface that paints above the native
/// video HWND — and auto-hide unless the cursor is over the window. Commands and dynamic display are
/// driven from code-behind (binding into the airspace Popup proved unreliable). Dragging and 16:9
/// resizing are handled here; <see cref="Window.AllowsTransparency"/> stays false so the video HWND
/// can render.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private const double MinContentWidth = 240;
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(2.5);

    private readonly DispatcherTimer _uiTimer;
    private readonly string _playGlyph;
    private readonly string _pauseGlyph;

    private DateTime _lastInside = DateTime.MinValue;
    private bool _placed;
    private bool _seeking;

    private bool _moving;
    private bool _resizing;
    private IInputElement? _captured;
    private Point _manipCursor;
    private double _manipLeft;
    private double _manipTop;
    private double _manipWidth;
    private double _dipScaleX = 1;
    private double _dipScaleY = 1;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        _playGlyph = (string)FindResource("Lumen.Icon.Play");
        _pauseGlyph = (string)FindResource("Lumen.Icon.Pause");
        PlayPauseGlyph.Text = _pauseGlyph;

        ControlsRoot.MouseMove += OnControlsMouseMove;
        ControlsRoot.MouseLeftButtonUp += OnManipEnd;

        SeekSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        SeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            Vm?.Playback.Seek(SeekSlider.Value);
            _seeking = false;
        };

        LocationChanged += (_, _) => RepositionControls();
        SizeChanged += (_, _) => RepositionControls();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => Tick();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                UpdateDisplay();
                _uiTimer.Start();
            }
            else
            {
                _uiTimer.Stop();
                ControlsPopup.IsOpen = false;
            }
        };
        Closed += (_, _) => _uiTimer.Stop();
    }

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_placed)
        {
            return;
        }

        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24;
        Top = work.Bottom - Height - 24;
        _placed = true;
    }

    private void Tick()
    {
        // Hover: show controls while the cursor is over the window (the video is a child HWND, so
        // window IsMouseOver never fires over it — poll the cursor position instead).
        if (_moving || _resizing || CursorInside())
        {
            _lastInside = DateTime.Now;
            if (!ControlsPopup.IsOpen)
            {
                ControlsPopup.IsOpen = true;
                RepositionControls();
            }
        }
        else if (ControlsPopup.IsOpen && DateTime.Now - _lastInside > HideAfter)
        {
            ControlsPopup.IsOpen = false;
        }

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var playback = vm.Playback;
        TitleText.Text = playback.NowPlayingTitle ?? string.Empty;
        PlayPauseGlyph.Text = playback.State == PlaybackState.Paused ? _playGlyph : _pauseGlyph;
        GoLiveButton.Visibility = playback.IsBehindLive ? Visibility.Visible : Visibility.Collapsed;

        var isVod = playback.IsVod;
        SeekSlider.Visibility = isVod ? Visibility.Visible : Visibility.Collapsed;
        TimeText.Visibility = isVod ? Visibility.Visible : Visibility.Collapsed;
        if (isVod)
        {
            if (!_seeking)
            {
                SeekSlider.Maximum = Math.Max(1, playback.DurationSeconds);
                SeekSlider.Value = Math.Clamp(playback.PositionSeconds, 0, SeekSlider.Maximum);
            }

            TimeText.Text = $"{FormatTime(playback.PositionSeconds)} / {FormatTime(playback.DurationSeconds)}";
        }
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private bool CursorInside()
    {
        GetCursorPos(out var point);
        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = matrix.Transform(new Point(point.X, point.Y));
        return dip.X >= Left && dip.X <= Left + Width && dip.Y >= Top && dip.Y <= Top + Height;
    }

    private void RepositionControls()
    {
        ControlsRoot.Width = ActualWidth;
        ControlsRoot.Height = ActualHeight;

        // Popups don't follow their placement target when the window moves — nudge the offset so the
        // controls stay glued during drag/resize.
        if (ControlsPopup.IsOpen)
        {
            var offset = ControlsPopup.HorizontalOffset;
            ControlsPopup.HorizontalOffset = offset + 0.5;
            ControlsPopup.HorizontalOffset = offset;
        }
    }

    // ---- commands (Click handlers use the confirmed window DataContext) ----

    private void OnExpand(object sender, RoutedEventArgs e) => Vm?.ExpandMiniCommand.Execute(null);

    private void OnClose(object sender, RoutedEventArgs e) => Vm?.CloseMiniCommand.Execute(null);

    private void OnPlayPause(object sender, RoutedEventArgs e) => Vm?.TogglePlayPauseCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e) => Vm?.Playback.Stop();

    private void OnGoToLive(object sender, RoutedEventArgs e) => _ = Vm?.Playback.GoToLiveAsync();

    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        PinButton.ToolTip = Topmost
            ? Lumen.App.Resources.Strings.Player_Unpin
            : Lumen.App.Resources.Strings.Player_Pin;
    }

    // ---- drag / resize ----

    private void OnMoveStart(object sender, MouseButtonEventArgs e)
    {
        // Don't start a window drag when the press lands on a control.
        if (IsOnControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        BeginManipulation(sender);
        _manipLeft = Left;
        _manipTop = Top;
        _moving = true;
        e.Handled = true;
    }

    private void OnResizeStart(object sender, MouseButtonEventArgs e)
    {
        BeginManipulation(sender);
        _manipWidth = Width;
        _resizing = true;
        e.Handled = true;
    }

    private void BeginManipulation(object handle)
    {
        GetCursorPos(out var point);
        _manipCursor = new Point(point.X, point.Y);

        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        _dipScaleX = matrix.M11 == 0 ? 1 : matrix.M11;
        _dipScaleY = matrix.M22 == 0 ? 1 : matrix.M22;

        _captured = handle as IInputElement;
        _captured?.CaptureMouse();
    }

    private void OnControlsMouseMove(object sender, MouseEventArgs e)
    {
        if (_moving)
        {
            GetCursorPos(out var point);
            Left = _manipLeft + ((point.X - _manipCursor.X) * _dipScaleX);
            Top = _manipTop + ((point.Y - _manipCursor.Y) * _dipScaleY);
        }
        else if (_resizing)
        {
            GetCursorPos(out var point);
            var width = Math.Max(MinContentWidth, _manipWidth + ((point.X - _manipCursor.X) * _dipScaleX));
            Width = width;
            Height = width * 9.0 / 16.0; // keep 16:9
        }
    }

    private void OnManipEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_moving && !_resizing)
        {
            return;
        }

        _moving = false;
        _resizing = false;
        _captured?.ReleaseMouseCapture();
        _captured = null;
    }

    private static bool IsOnControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or Slider or Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>Diagnostics only: force the controls open and report the realized state.</summary>
    internal string OpenControlsForDiagnostics()
    {
        ControlsPopup.IsOpen = true;
        RepositionControls();
        UpdateLayout();
        ControlsRoot.UpdateLayout();

        var buttons = new List<ButtonBase>();
        CollectVisual(ControlsRoot, buttons);
        var clickWired = buttons.Count(b => b is Button); // command buttons use Click handlers
        var hasVm = Vm is not null;
        return $"buttons={buttons.Count} clickButtons={clickWired} vm={hasVm}";
    }

    private static void CollectVisual<T>(DependencyObject root, List<T> into)
        where T : DependencyObject
    {
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                into.Add(typed);
            }

            CollectVisual(child, into);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
