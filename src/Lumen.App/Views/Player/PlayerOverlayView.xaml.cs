using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lumen.App.ViewModels;

namespace Lumen.App.Views.Player;

/// <summary>
/// Overlay hosted inside the shared VideoView (the only airspace that renders above the
/// video). Visual-only concerns live here: the 3-second auto-hide, cursor hiding, the
/// player keyboard map, and double-click fullscreen. Mini/PiP controls live in
/// <see cref="Controls.MiniPlayerWindow"/>, not here.
/// </summary>
public partial class PlayerOverlayView : UserControl
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _hideTimer;

    public PlayerOverlayView()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => TryHideOverlay();

        MouseMove += (_, _) => ShowOverlay();
        PreviewMouseDown += (_, _) => ShowOverlay();
        PreviewKeyDown += OnPlayerKeyDown;
        MouseDoubleClick += OnDoubleClick;
        Unloaded += (_, _) => _hideTimer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                ShowOverlay();
                Focusable = true;
                Keyboard.Focus(this);
            }
        };
    }

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    private void ShowOverlay()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.IsOverlayVisible = true;
        Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void TryHideOverlay()
    {
        _hideTimer.Stop();
        if (ViewModel is not { } vm || !vm.Playback.IsFullPlayerActive)
        {
            return;
        }

        // Keep the chrome up while a picker or the channel list is open.
        if (vm.IsChannelListOpen ||
            AudioMenuButton.IsChecked == true ||
            SubtitleMenuButton.IsChecked == true)
        {
            _hideTimer.Start();
            return;
        }

        vm.IsOverlayVisible = false;
        Cursor = Cursors.None;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } vm && vm.Playback.IsFullPlayerActive)
        {
            vm.ToggleFullscreenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPlayerKeyDown(object sender, KeyEventArgs e)
    {
        ShowOverlay();
        if (ViewModel is { } vm && vm.HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }
}
