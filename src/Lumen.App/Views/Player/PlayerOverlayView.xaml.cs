using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lumen.App.Services.Playback;
using Lumen.App.ViewModels;

namespace Lumen.App.Views.Player;

/// <summary>
/// Overlay hosted inside the shared VideoView (the only airspace that renders above the
/// video). Visual-only concerns live here: the 3-second auto-hide, cursor hiding, the
/// player keyboard map, double-click fullscreen, and the VOD seek bar. Mini/PiP controls
/// live in <see cref="Controls.MiniPlayerWindow"/>, not here.
/// </summary>
public partial class PlayerOverlayView : UserControl
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    /// <summary>Releasing the live thumb this close to the right edge just rejoins live.</summary>
    private const double LiveEdgeSnapSeconds = 45;

    /// <summary>Live seek window without EPG data: the last two hours.</summary>
    private static readonly TimeSpan DefaultLiveWindow = TimeSpan.FromHours(2);

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _liveTickTimer;
    private PlaybackService? _observedPlayback;
    private bool _seeking;
    private bool _liveSeeking;
    private DateTimeOffset _liveWindowStartUtc;

    public PlayerOverlayView()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => TryHideOverlay();

        // The live timeline moves even when nothing about playback "changes" (the window's
        // right edge is the wall clock), so it ticks rather than reacting to INPC.
        _liveTickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTickTimer.Tick += (_, _) => UpdateLiveSeekBar();

        MouseMove += (_, _) => ShowOverlay();
        PreviewMouseDown += (_, _) => ShowOverlay();
        PreviewKeyDown += OnPlayerKeyDown;
        MouseDoubleClick += OnDoubleClick;
        Loaded += (_, _) => _liveTickTimer.Start();
        Unloaded += (_, _) =>
        {
            _hideTimer.Stop();
            _liveTickTimer.Stop();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                ShowOverlay();
                Focusable = true;
                Keyboard.Focus(this);
            }
        };

        // Seek gesture (same shape as MiniPlayerWindow): position updates leave the slider
        // alone mid-drag, and the actual seek happens once on release.
        SeekSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        SeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            ViewModel?.Playback.Seek(SeekSlider.Value);
            _seeking = false;
        };
        SeekSlider.ValueChanged += (_, _) =>
        {
            if (_seeking)
            {
                UpdateSeekText(); // scrub feedback: elapsed follows the thumb, not playback
            }
        };

        // Live catch-up gesture: the slider maps wall-clock time across the seek window;
        // committing opens a fresh archive stream (or rejoins live near the right edge).
        LiveSeekSlider.PreviewMouseLeftButtonDown += (_, _) => _liveSeeking = true;
        LiveSeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (ViewModel is { } vm && vm.Playback.CanSeekLive)
            {
                if (LiveSeekSlider.Maximum - LiveSeekSlider.Value <= LiveEdgeSnapSeconds)
                {
                    _ = vm.Playback.GoToLiveAsync();
                }
                else
                {
                    _ = vm.Playback.SeekLiveAsync(_liveWindowStartUtc.AddSeconds(LiveSeekSlider.Value));
                }
            }

            _liveSeeking = false;
        };
        LiveSeekSlider.ValueChanged += (_, _) =>
        {
            if (_liveSeeking)
            {
                UpdateLiveSeekText(); // scrub feedback: the clock follows the thumb
            }
        };

        DataContextChanged += (_, _) => ObservePlayback();
    }

    private PlayerViewModel? ViewModel => DataContext as PlayerViewModel;

    // ---- VOD seek bar ----

    private void ObservePlayback()
    {
        if (_observedPlayback is { } previous)
        {
            previous.PropertyChanged -= OnPlaybackPropertyChanged;
        }

        _observedPlayback = ViewModel?.Playback;
        if (_observedPlayback is { } playback)
        {
            playback.PropertyChanged += OnPlaybackPropertyChanged;
            UpdateSeekBar();
        }
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackService.PositionSeconds)
            or nameof(PlaybackService.DurationSeconds)
            or nameof(PlaybackService.IsVod))
        {
            UpdateSeekBar();
        }
        else if (e.PropertyName is nameof(PlaybackService.CanSeekLive)
            or nameof(PlaybackService.LiveDelaySeconds))
        {
            UpdateLiveSeekBar();
        }
    }

    private void UpdateSeekBar()
    {
        if (ViewModel is not { Playback.IsVod: true } vm)
        {
            return; // the bar is collapsed for live; nothing to keep in sync
        }

        if (!_seeking)
        {
            SeekSlider.Maximum = Math.Max(1, vm.Playback.DurationSeconds);
            SeekSlider.Value = Math.Clamp(vm.Playback.PositionSeconds, 0, SeekSlider.Maximum);
        }

        UpdateSeekText();
    }

    private void UpdateSeekText()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var position = _seeking ? SeekSlider.Value : vm.Playback.PositionSeconds;
        SeekPositionText.Text = FormatTime(position);
        SeekDurationText.Text = FormatTime(vm.Playback.DurationSeconds);
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    // ---- live catch-up seek bar ----

    /// <summary>
    /// Rebuilds the live timeline: left edge is the current programme's start (or a rolling
    /// two-hour window without EPG), right edge is the live moment, thumb is the playhead.
    /// </summary>
    private void UpdateLiveSeekBar()
    {
        if (ViewModel is not { } vm || !vm.Playback.CanSeekLive || !vm.Playback.IsFullPlayerActive)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var playhead = now.AddSeconds(-vm.Playback.LiveDelaySeconds);
        var windowStart = vm.NowProgrammeStartUtc ?? now - DefaultLiveWindow;
        if (playhead < windowStart.AddMinutes(1))
        {
            // Rewound past the window's left edge (e.g. into the previous programme after a
            // broadcast rollover) — slide the window so the playhead stays on the bar.
            windowStart = playhead.AddMinutes(-5);
        }

        _liveWindowStartUtc = windowStart;
        if (!_liveSeeking)
        {
            LiveSeekSlider.Maximum = Math.Max(1, (now - windowStart).TotalSeconds);
            LiveSeekSlider.Value = Math.Clamp(
                (playhead - windowStart).TotalSeconds, 0, LiveSeekSlider.Maximum);
        }

        UpdateLiveSeekText();
    }

    private void UpdateLiveSeekText()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var shown = _liveSeeking
            ? _liveWindowStartUtc.AddSeconds(LiveSeekSlider.Value)
            : DateTimeOffset.UtcNow.AddSeconds(-vm.Playback.LiveDelaySeconds);
        LivePositionText.Text = shown.ToLocalTime().ToString("HH:mm:ss");
    }

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
