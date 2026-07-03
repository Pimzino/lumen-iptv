using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Controls;
using Lumen.App.Services.Playback;
using Lumen.App.ViewModels;
using Lumen.App.Views.Debug;
using Lumen.App.Views.Player;

namespace Lumen.App;

/// <summary>Application shell: title bar, navigation rail, page host, player layers, toasts.</summary>
public partial class MainWindow : Window
{
    private const int MonitorDefaultToNearest = 2;

    private readonly PlayerViewModel _playerViewModel;
    private readonly PlaybackService _playback;
    private MiniPlayerWindow? _miniWindow;

    private bool _isTrueFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private Rect _preFullscreenBounds;

    public MainWindow(ShellViewModel viewModel, PlaybackService playback, PlayerViewModel playerViewModel, IMessenger messenger)
    {
        InitializeComponent();
        DataContext = viewModel;
        _playerViewModel = playerViewModel;
        _playback = playback;

        // The overlay lives inside the shared VideoView — the only airspace above video.
        playback.SetOverlay(new PlayerOverlayView { DataContext = playerViewModel });

        messenger.Register<FullscreenToggledMessage>(this, (_, message) => ApplyFullscreen(message.IsFullscreen));

        // The windowed player's bar shows real window buttons, so keep the maximize glyph in sync.
        StateChanged += (_, _) => _playerViewModel.IsWindowMaximized = WindowState == WindowState.Maximized;

        // The mini player is a separate always-on-top window (picture-in-picture).
        playback.PropertyChanged += OnPlaybackPropertyChanged;
        Closed += (_, _) => _miniWindow?.Close();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsMiniPlayerActive))
        {
            SetMiniPlayerVisible(_playback.IsMiniPlayerActive);
        }
    }

    private void SetMiniPlayerVisible(bool visible)
    {
        if (visible)
        {
            _miniWindow ??= new MiniPlayerWindow { DataContext = _playerViewModel };
            _miniWindow.Show();
        }
        else
        {
            _miniWindow?.Hide();
        }
    }

    /// <summary>
    /// True fullscreen: borderless, covering the taskbar. A plain <c>Maximized</c> stops at the work
    /// area (taskbar still visible), so we go topmost and size the window to the whole monitor.
    /// </summary>
    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen == _isTrueFullscreen)
        {
            return;
        }

        _isTrueFullscreen = fullscreen;

        if (fullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenBounds = new Rect(Left, Top, Width, Height);

            LumenChrome.SetIsImmersive(this, true);
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal; // so explicit bounds take effect
            }

            ResizeMode = ResizeMode.NoResize;
            var bounds = MonitorBoundsDip();
            Topmost = true; // render above the always-on-top taskbar
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }
        else
        {
            LumenChrome.SetIsImmersive(this, false);
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullscreenState;
            if (_preFullscreenState == WindowState.Normal)
            {
                Left = _preFullscreenBounds.Left;
                Top = _preFullscreenBounds.Top;
                Width = _preFullscreenBounds.Width;
                Height = _preFullscreenBounds.Height;
            }
        }
    }

    /// <summary>Full bounds (not work area) of the monitor the window is on, in device-independent units.</summary>
    private Rect MonitorBoundsDip()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return new Rect(Left, Top, Width, Height);
        }

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
        var bottomRight = transform.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Hidden design-system gallery (debug aid): Ctrl+Shift+G.
        if (e.Key == Key.G &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            new GalleryWindow { Owner = this }.Show();
            e.Handled = true;
            return;
        }

        // Player keyboard map, regardless of which HWND holds focus.
        if (Keyboard.Modifiers == ModifierKeys.None && _playerViewModel.HandleKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }
}
