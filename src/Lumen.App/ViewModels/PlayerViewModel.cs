using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>Raised when the player wants the window fullscreened (or restored).</summary>
public sealed record FullscreenToggledMessage(bool IsFullscreen);

/// <summary>
/// Drives the player overlay (all surfaces): transport controls, now/next with live
/// progress, zap banner, quick channel list, track pickers, and error/reconnect states.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject,
    IRecipient<ChannelChangedMessage>,
    IRecipient<EpgRefreshedMessage>
{
    private readonly IEpgRepository _epgRepository;
    private readonly ISessionService _session;
    private readonly IMessenger _messenger;
    private readonly IClock _clock;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _bannerTimer;

    public PlayerViewModel(
        PlaybackService playback,
        IEpgRepository epgRepository,
        ISessionService session,
        IClock clock,
        IMessenger messenger)
    {
        Playback = playback;
        _epgRepository = epgRepository;
        _session = session;
        _clock = clock;
        _messenger = messenger;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            IsBannerVisible = false;
        };

        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlaybackService.ReconnectAttempt))
            {
                ReconnectStatus = Resources.Strings.Format(
                    Resources.Strings.Player_ReconnectingFormat, Playback.ReconnectAttempt);
            }
            else if (e.PropertyName is nameof(PlaybackService.IsVod) && playback.IsVod)
            {
                // Now/next is live-EPG data; a VOD start must clear the last channel's leftovers.
                SetNowNext(null);
            }
        };

        messenger.RegisterAll(this);
    }

    [ObservableProperty]
    private string? _reconnectStatus;

    /// <summary>Concrete playback service (INPC) for direct binding.</summary>
    public PlaybackService Playback { get; }

    // ---- overlay state ----

    [ObservableProperty]
    private bool _isOverlayVisible = true;

    [ObservableProperty]
    private bool _isChannelListOpen;

    [ObservableProperty]
    private bool _isFullscreen;

    // ---- now/next ----

    [ObservableProperty]
    private string? _nowTitle;

    [ObservableProperty]
    private string? _nowTimeRange;

    [ObservableProperty]
    private double _nowProgress;

    [ObservableProperty]
    private string? _nextTitle;

    private Programme? _nowProgramme;

    // ---- zap banner ----

    [ObservableProperty]
    private bool _isBannerVisible;

    [ObservableProperty]
    private string? _bannerChannelName;

    [ObservableProperty]
    private string? _bannerNowTitle;

    [ObservableProperty]
    private string? _bannerNextTitle;

    /// <summary>Channels shown in the quick list — the current zap list.</summary>
    [ObservableProperty]
    private IReadOnlyList<Channel> _quickChannels = [];

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void TogglePlayPause() => Playback.TogglePause();

    [RelayCommand]
    private void ToggleMute() => Playback.IsMuted = !Playback.IsMuted;

    [RelayCommand]
    private void VolumeUp() => Playback.Volume = Math.Min(100, Playback.Volume + 5);

    [RelayCommand]
    private void VolumeDown() => Playback.Volume = Math.Max(0, Playback.Volume - 5);

    [RelayCommand]
    private Task ZapUpAsync() => Playback.ZapAsync(-1);

    [RelayCommand]
    private Task ZapDownAsync() => Playback.ZapAsync(+1);

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        _messenger.Send(new FullscreenToggledMessage(IsFullscreen));
    }

    [RelayCommand]
    private void ToggleChannelList() => IsChannelListOpen = !IsChannelListOpen;

    [RelayCommand]
    private void CycleAspect() => Playback.CycleAspect();

    [RelayCommand]
    private void SelectAudioTrack(TrackOption? track)
    {
        if (track is not null)
        {
            Playback.SelectAudioTrack(track.Id);
        }
    }

    [RelayCommand]
    private void SelectSubtitleTrack(TrackOption? track)
    {
        if (track is not null)
        {
            Playback.SelectSubtitleTrack(track.Id);
        }
    }

    [RelayCommand]
    private async Task PlayChannelAsync(Channel? channel)
    {
        if (channel is null)
        {
            return;
        }

        IsChannelListOpen = false;
        await Playback.PlayChannelAsync(channel, null, preview: false, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Playback.CurrentChannel is { } channel)
        {
            await Playback.PlayChannelAsync(channel, null, preview: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// Esc / back arrow: close the channel list, then leave fullscreen, then return to browsing —
    /// the video keeps playing as the muted list preview. The floating PiP is a separate,
    /// dedicated button.
    /// </summary>
    [RelayCommand]
    private void Back()
    {
        if (IsChannelListOpen)
        {
            IsChannelListOpen = false;
            return;
        }

        if (IsFullscreen)
        {
            ToggleFullscreen();
            return;
        }

        Playback.ExitFullPlayer(PlayerExitMode.Browse);
    }

    /// <summary>Collapse the full player into the floating picture-in-picture window.</summary>
    [RelayCommand]
    private void ToMiniPlayer()
    {
        if (IsFullscreen)
        {
            ToggleFullscreen();
        }

        Playback.ExitFullPlayer(PlayerExitMode.MiniPlayer);
    }

    [RelayCommand]
    private void ExpandMini() => Playback.EnterFullPlayer();

    [RelayCommand]
    private void CloseMini()
    {
        if (IsFullscreen)
        {
            ToggleFullscreen();
        }

        Playback.Stop();
    }

    // ---- window controls (the player bar replaces the hidden title bar) ----

    /// <summary>Whether the shell window is maximized — drives the maximize/restore glyph.</summary>
    [ObservableProperty]
    private bool _isWindowMaximized;

    [RelayCommand]
    private void MinimizeWindow()
    {
        if (Application.Current.MainWindow is { } window)
        {
            SystemCommands.MinimizeWindow(window);
        }
    }

    [RelayCommand]
    private void ToggleMaximizeWindow()
    {
        if (Application.Current.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }

    [RelayCommand]
    private void CloseWindow()
    {
        if (Application.Current.MainWindow is { } window)
        {
            SystemCommands.CloseWindow(window);
        }
    }

    // ------------------------------------------------------------------ messages & ticks

    public void Receive(ChannelChangedMessage message)
    {
        QuickChannels = Playback.ZapList ?? QuickChannels;
        BannerChannelName = message.Channel.Name;
        BannerNowTitle = null;
        BannerNextTitle = null;
        _ = LoadNowNextAsync(message.Channel);

        if (Playback.IsFullPlayerActive)
        {
            IsBannerVisible = true;
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
    }

    public void Receive(EpgRefreshedMessage message)
    {
        if (Playback.CurrentChannel is { } channel)
        {
            _ = LoadNowNextAsync(channel);
        }
    }

    private async Task LoadNowNextAsync(Channel channel)
    {
        try
        {
            var profile = _session.CurrentProfile;
            if (profile is null)
            {
                SetNowNext(null);
                return;
            }

            var mappings = await _epgRepository.GetMappingsAsync(profile.Id, CancellationToken.None);
            var xmltvId = mappings.FirstOrDefault(m => m.ChannelId == channel.Id)?.XmltvId
                ?? channel.EpgChannelId;
            if (string.IsNullOrEmpty(xmltvId))
            {
                SetNowNext(null);
                return;
            }

            var nowNext = await _epgRepository.GetNowNextAsync(
                profile.Id, [xmltvId], _clock.UtcNow.ToUnixTimeSeconds(), CancellationToken.None);
            SetNowNext(nowNext.GetValueOrDefault(xmltvId));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Now/next lookup failed");
            SetNowNext(null);
        }
    }

    private void SetNowNext(NowNext? nowNext)
    {
        _nowProgramme = nowNext?.Now;
        NowTitle = nowNext?.Now?.Title;
        NowTimeRange = nowNext?.Now is { } now
            ? $"{now.Start.ToLocalTime():HH:mm} – {now.Stop.ToLocalTime():HH:mm}"
            : null;
        NextTitle = nowNext?.Next?.Title;
        BannerNowTitle = NowTitle;
        BannerNextTitle = NextTitle;
        UpdateProgress();
    }

    /// <summary>
    /// The player keyboard map (spec §4.4): Space play/pause, F fullscreen, M mute,
    /// ↑/↓ zap, ←/→ volume, Esc back, Enter channel list. Routed from both the overlay
    /// and the main window so focus quirks never eat a key.
    /// </summary>
    public bool HandleKey(System.Windows.Input.Key key)
    {
        if (!Playback.IsFullPlayerActive)
        {
            return false;
        }

        switch (key)
        {
            case System.Windows.Input.Key.Space:
                TogglePlayPause();
                return true;
            case System.Windows.Input.Key.F:
                ToggleFullscreen();
                return true;
            case System.Windows.Input.Key.M:
                ToggleMute();
                return true;
            case System.Windows.Input.Key.Up:
                _ = ZapUpAsync();
                return true;
            case System.Windows.Input.Key.Down:
                _ = ZapDownAsync();
                return true;
            case System.Windows.Input.Key.Left:
                VolumeDown();
                return true;
            case System.Windows.Input.Key.Right:
                VolumeUp();
                return true;
            case System.Windows.Input.Key.Enter:
                ToggleChannelList();
                return true;
            case System.Windows.Input.Key.Escape:
                Back();
                return true;
            default:
                return false;
        }
    }

    private void UpdateProgress()
    {
        if (_nowProgramme is { } programme)
        {
            NowProgress = programme.ProgressAt(_clock.UtcNow) * 100;
            if (_clock.UtcNow.ToUnixTimeSeconds() > programme.StopUtc && Playback.CurrentChannel is { } channel)
            {
                _ = LoadNowNextAsync(channel); // rolled over to the next programme
            }
        }
        else
        {
            NowProgress = 0;
        }
    }
}
