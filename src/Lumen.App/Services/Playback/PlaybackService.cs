using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Playback;

/// <summary>
/// Default <see cref="IPlaybackService"/>. One LibVLC + one MediaPlayer + one VideoView
/// for the process; robust against stream drops with 1/2/4/8/8s backoff (max 5 attempts).
/// LibVLC events arrive on native threads — every state mutation marshals to the dispatcher,
/// and every player command runs off the event thread.
/// </summary>
public sealed partial class PlaybackService : ObservableObject, IPlaybackService
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8),
    ];

    private readonly ISessionService _session;
    private readonly ISettingsRepository _settings;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;
    private readonly ILogger<PlaybackService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<VideoSurfaceKind, Decorator> _surfaces = [];
    private readonly DispatcherTimer _positionTimer;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private VideoView? _videoView;
    private System.Windows.UIElement? _overlayContent;
    private VideoSurfaceKind _activeSurface = VideoSurfaceKind.Preview;

    private string? _currentUrl;
    private Channel? _currentChannel;
    private IReadOnlyList<Channel>? _zapList;
    private bool _isPreviewContext;
    private bool _userStopped;
    private CancellationTokenSource? _reconnectCts;
    private int _playRequestSequence;

    // Serializes native Play/Stop so overlapping requests (fast zapping, stop-during-open) never
    // interleave on the single shared MediaPlayer, and a superseded request skips its Play.
    private readonly SemaphoreSlim _playerGate = new(1, 1);

    // VOD state
    private VodPlayRequest? _currentVod;
    private double _pendingResumeSeconds;
    private bool _resumeApplied;

    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Idle;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _reconnectAttempt;

    [ObservableProperty]
    private int _volume = 80;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private AspectMode _aspect = AspectMode.Fit;

    [ObservableProperty]
    private IReadOnlyList<TrackOption> _audioTracks = [];

    [ObservableProperty]
    private IReadOnlyList<TrackOption> _subtitleTracks = [];

    [ObservableProperty]
    private int _activeAudioTrack = -1;

    [ObservableProperty]
    private int _activeSubtitleTrack = -1;

    [ObservableProperty]
    private bool _isFullPlayerActive;

    [ObservableProperty]
    private bool _isMiniPlayerActive;

    [ObservableProperty]
    private bool _isVod;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    public PlaybackService(
        ISessionService session,
        ISettingsRepository settings,
        IWatchHistoryRepository watchHistory,
        IClock clock,
        IMessenger messenger,
        ILogger<PlaybackService> logger)
    {
        _session = session;
        _settings = settings;
        _watchHistory = watchHistory;
        _clock = clock;
        _messenger = messenger;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current.Dispatcher;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _positionTimer.Tick += (_, _) => OnPositionTick();
    }

    /// <summary>Fraction watched, clamped to [0, 1]; 0 for live or unknown duration.</summary>
    public double Progress => DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) : 0;

    public Channel? CurrentChannel
    {
        get => _currentChannel;
        private set => SetProperty(ref _currentChannel, value);
    }

    /// <summary>Display title of whatever is playing: the VOD title, else the live channel name.</summary>
    public string? NowPlayingTitle => _currentVod?.Title ?? _currentChannel?.Name;

    /// <summary>Artwork for ambient effects: the VOD poster, else the channel logo.</summary>
    public string? NowPlayingArtUrl => _currentVod?.PosterUrl ?? _currentChannel?.LogoUrl;

    private void NotifyNowPlayingChanged()
    {
        OnPropertyChanged(nameof(NowPlayingTitle));
        OnPropertyChanged(nameof(NowPlayingArtUrl));
    }

    /// <summary>The ordered channel list ↑/↓ zapping walks (also feeds the quick list).</summary>
    public IReadOnlyList<Channel>? ZapList => _zapList;

    public MediaPlayer? Player => _player;

    /// <summary>Diagnostics only: the overlay element installed by the shell.</summary>
    internal System.Windows.UIElement? OverlayForDiagnostics => _overlayContent;

    /// <summary>
    /// Installs the WPF overlay rendered above the video (VideoView.Content is the only
    /// airspace that draws over the native surface). Called once by the shell window.
    /// </summary>
    public void SetOverlay(System.Windows.UIElement overlay)
    {
        _overlayContent = overlay;
        if (_videoView is not null)
        {
            _videoView.Content = overlay;
        }
    }

    // ------------------------------------------------------------------ playback

    public async Task PlayChannelAsync(
        Channel channel, IReadOnlyList<Channel>? zapList, bool preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var sequence = Interlocked.Increment(ref _playRequestSequence);

        await EnsureInitializedAsync(cancellationToken);

        var url = ResolveStreamUrl(channel);
        if (url is null)
        {
            State = PlaybackState.Error;
            ErrorMessage = "This channel has no playable stream URL.";
            return;
        }

        if (sequence != _playRequestSequence)
        {
            return; // superseded by a newer request (fast zapping)
        }

        CancelReconnect();
        _userStopped = false;

        // Leaving a VOD for a live channel: persist its resume point and drop VOD state so
        // position ticks and failure handling run in live mode again.
        SaveVodProgress();
        _positionTimer.Stop();
        _currentVod = null;
        IsVod = false;
        PositionSeconds = 0;
        DurationSeconds = 0;

        _currentChannel = channel;
        OnPropertyChanged(nameof(CurrentChannel));
        NotifyNowPlayingChanged();
        if (zapList is not null && !ReferenceEquals(zapList, _zapList))
        {
            _zapList = zapList;
            OnPropertyChanged(nameof(ZapList));
        }
        _isPreviewContext = preview;
        _currentUrl = url;
        ErrorMessage = null;
        ReconnectAttempt = 0;
        State = PlaybackState.Opening;

        ApplyMute();
        await StartPlaybackAsync(url, channel, sequence);

        // A newer request (or Stop, which bumps the sequence) may have arrived while we were
        // opening; don't announce a channel the user has already moved past.
        if (sequence == _playRequestSequence)
        {
            _messenger.Send(new ChannelChangedMessage(channel));
        }
    }

    private async Task StartPlaybackAsync(string url, Channel channel, int sequence)
    {
        var player = _player!;
        await Task.Run(async () =>
        {
            await _playerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // LibVLC ignores Play() from the Ended state and can surface stale end-events;
                // an explicit Stop() makes the state machine deterministic before every start.
                player.Stop();

                // Superseded while we waited for the gate (fast zap / Stop) — don't start.
                if (sequence != Volatile.Read(ref _playRequestSequence))
                {
                    return;
                }

                using var media = new Media(_libVlc!, new Uri(url));
                media.AddOption($":network-caching={_networkCachingMs}");
                media.AddOption($":http-user-agent={EffectiveUserAgent(channel)}");
                if (!string.IsNullOrEmpty(channel.Referrer))
                {
                    media.AddOption($":http-referrer={channel.Referrer}");
                }

                player.Play(media);
            }
            finally
            {
                _playerGate.Release();
            }
        });
    }

    /// <summary>Plays a movie or series episode, seeking to its resume position, and tracks progress.</summary>
    public async Task PlayVodAsync(VodPlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sequence = Interlocked.Increment(ref _playRequestSequence);
        await EnsureInitializedAsync(cancellationToken);

        CancelReconnect();
        _userStopped = false;

        // A previous VOD may still be playing (kept alive as a browse preview) — bank its position.
        SaveVodProgress();

        _currentChannel = null;
        OnPropertyChanged(nameof(CurrentChannel));
        _currentVod = request;
        NotifyNowPlayingChanged();
        _isPreviewContext = false;
        _currentUrl = request.Url;
        _pendingResumeSeconds = request.ResumeSeconds;
        _resumeApplied = false;
        IsVod = true;
        PositionSeconds = request.ResumeSeconds;
        DurationSeconds = 0;
        ErrorMessage = null;
        ReconnectAttempt = 0;
        State = PlaybackState.Opening;

        ApplyMute();
        await StartVodAsync(request, sequence);
        EnterFullPlayer();
    }

    private async Task StartVodAsync(VodPlayRequest request, int sequence)
    {
        var player = _player!;
        await Task.Run(async () =>
        {
            await _playerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                player.Stop();
                if (sequence != Volatile.Read(ref _playRequestSequence))
                {
                    return;
                }

                using var media = new Media(_libVlc!, new Uri(request.Url));
                media.AddOption($":network-caching={_networkCachingMs}");
                media.AddOption($":http-user-agent={EffectiveUserAgent(null)}");
                player.Play(media);
            }
            finally
            {
                _playerGate.Release();
            }
        });
    }

    public async Task ZapAsync(int direction)
    {
        var list = _zapList;
        var current = _currentChannel;
        if (list is null || list.Count == 0 || current is null)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == current.Id)
            {
                index = i;
                break;
            }
        }

        var next = list[(index + direction + list.Count) % list.Count];
        await PlayChannelAsync(next, list, _isPreviewContext, CancellationToken.None);
    }

    public void TogglePause()
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        Task.Run(() =>
        {
            if (player.CanPause)
            {
                player.Pause();
            }
        });
    }

    public void Stop()
    {
        _userStopped = true;
        CancelReconnect();
        Interlocked.Increment(ref _playRequestSequence);

        // Persist the VOD resume position before tearing down.
        SaveVodProgress();
        _positionTimer.Stop();
        _currentVod = null;
        IsVod = false;

        _currentChannel = null;
        OnPropertyChanged(nameof(CurrentChannel));
        NotifyNowPlayingChanged();
        _currentUrl = null;
        State = PlaybackState.Idle;
        ErrorMessage = null;
        IsMiniPlayerActive = false;
        IsFullPlayerActive = false;

        var player = _player;
        if (player is not null)
        {
            // Serialize with in-flight Play calls so a stop can't be undone by a slower Play.
            Task.Run(async () =>
            {
                await _playerGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    player.Stop();
                }
                finally
                {
                    _playerGate.Release();
                }
            });
        }
    }

    /// <summary>Seeks the current VOD stream to an absolute position in seconds.</summary>
    public void Seek(double seconds)
    {
        var player = _player;
        if (player is null || !IsVod || DurationSeconds <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = clamped;
        Task.Run(() => player.Time = (long)(clamped * 1000));
    }

    private void OnPositionTick()
    {
        var player = _player;
        if (player is null || !IsVod)
        {
            return;
        }

        if (player.Length > 0)
        {
            DurationSeconds = player.Length / 1000.0;
        }

        if (State == PlaybackState.Playing)
        {
            PositionSeconds = player.Time / 1000.0;
        }
    }

    /// <summary>Writes the current VOD position to watch history (throttled to meaningful progress).</summary>
    private void SaveVodProgress()
    {
        if (_currentVod is not { } vod || _session.CurrentProfile is not { } profile || DurationSeconds <= 0)
        {
            return;
        }

        // Treat "finished" (>95%) as position 0 so it doesn't nag to resume the last seconds.
        var fraction = PositionSeconds / DurationSeconds;
        var storedPosition = fraction is > 0.95 or < 0.02 ? 0 : PositionSeconds;

        var entry = new WatchHistoryEntry
        {
            ProfileId = profile.Id,
            ItemKind = vod.Kind,
            ItemKey = vod.ItemKey,
            Title = vod.Title,
            PosterUrl = vod.PosterUrl,
            PositionSeconds = storedPosition,
            DurationSeconds = DurationSeconds,
            WatchedUtc = _clock.UtcNow.ToUnixTimeSeconds(),
        };
        _ = _watchHistory.UpsertAsync(entry, CancellationToken.None);
    }

    // ------------------------------------------------------------------ tracks & aspect

    public void SelectAudioTrack(int id)
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        Task.Run(() => player.SetAudioTrack(id));
        ActiveAudioTrack = id;
    }

    public void SelectSubtitleTrack(int id)
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        Task.Run(() => player.SetSpu(id));
        ActiveSubtitleTrack = id;
    }

    public void CycleAspect()
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        Aspect = Aspect switch
        {
            AspectMode.Fit => AspectMode.Ratio16X9,
            AspectMode.Ratio16X9 => AspectMode.Ratio4X3,
            AspectMode.Ratio4X3 => AspectMode.Fill,
            _ => AspectMode.Fit,
        };

        switch (Aspect)
        {
            case AspectMode.Ratio16X9:
                player.AspectRatio = "16:9";
                player.Scale = 0;
                break;
            case AspectMode.Ratio4X3:
                player.AspectRatio = "4:3";
                player.Scale = 0;
                break;
            case AspectMode.Fill:
                var host = _surfaces.GetValueOrDefault(_activeSurface);
                if (host is { ActualWidth: > 0, ActualHeight: > 0 })
                {
                    player.AspectRatio = $"{(int)host.ActualWidth}:{(int)host.ActualHeight}";
                }

                player.Scale = 0;
                break;
            default:
                player.AspectRatio = null;
                player.Scale = 0;
                break;
        }
    }

    partial void OnVolumeChanged(int value)
    {
        var player = _player;
        if (player is not null)
        {
            player.Volume = Math.Clamp(value, 0, 100);
        }

        _ = _settings.SetAsync(0, "volume", value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CancellationToken.None);
    }

    partial void OnIsMutedChanged(bool value) => ApplyMute();

    private void ApplyMute()
    {
        var player = _player;
        if (player is not null)
        {
            player.Mute = IsMuted || _isPreviewContext;
        }
    }

    // ------------------------------------------------------------------ full/mini player

    public void EnterFullPlayer()
    {
        _isPreviewContext = false;
        ApplyMute();
        IsMiniPlayerActive = false;
        IsFullPlayerActive = true;
        ActivateSurface(VideoSurfaceKind.FullPlayer);
    }

    public void ExitFullPlayer(PlayerExitMode mode)
    {
        var isLive = State is PlaybackState.Playing or PlaybackState.Buffering
            or PlaybackState.Opening or PlaybackState.Reconnecting or PlaybackState.Paused;

        // Nothing to keep alive — just stop, whatever the requested mode.
        if (!isLive || mode == PlayerExitMode.Stop)
        {
            Stop();
            return;
        }

        switch (mode)
        {
            case PlayerExitMode.MiniPlayer:
                // The mini player is a real watch surface, not a muted preview — restore audio.
                _isPreviewContext = false;
                ApplyMute();
                IsFullPlayerActive = false;
                IsMiniPlayerActive = true;
                ActivateSurface(VideoSurfaceKind.MiniPlayer);
                break;

            case PlayerExitMode.Browse:
            default:
                // Hide the player overlay and hand the video back to the browsing (preview) surface;
                // playback keeps running behind whatever page the user returns to.
                IsFullPlayerActive = false;
                IsMiniPlayerActive = false;
                _isPreviewContext = true;
                ApplyMute();
                ActivateSurface(VideoSurfaceKind.Preview);
                break;
        }
    }

    // ------------------------------------------------------------------ surfaces

    public void RegisterSurface(VideoSurfaceKind kind, Decorator host)
    {
        _surfaces[kind] = host;
        if (kind == _activeSurface)
        {
            AttachViewTo(host);
        }
    }

    public void UnregisterSurface(VideoSurfaceKind kind, Decorator host)
    {
        if (_surfaces.TryGetValue(kind, out var registered) && registered == host)
        {
            _surfaces.Remove(kind);
            if (_videoView is not null && host.Child == _videoView)
            {
                host.Child = null;
            }
        }
    }

    /// <summary>Moves the shared video view to the named surface slot.</summary>
    public void ActivateSurface(VideoSurfaceKind kind)
    {
        _activeSurface = kind;
        if (_surfaces.TryGetValue(kind, out var host))
        {
            AttachViewTo(host);
        }
    }

    private void AttachViewTo(Decorator host)
    {
        if (_videoView is null)
        {
            return;
        }

        if (_videoView.Parent is Decorator previous && previous != host)
        {
            previous.Child = null;
        }

        if (host.Child != _videoView)
        {
            host.Child = _videoView;
        }
    }

    // ------------------------------------------------------------------ init & events

    private int _networkCachingMs = 1500;
    private readonly object _initGate = new();
    private Task? _initTask;

    /// <summary>
    /// Initializes LibVLC + the MediaPlayer exactly once. Concurrent first-play requests share
    /// one init Task rather than each racing to build (and leak) a second player.
    /// </summary>
    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_initGate)
        {
            return _initTask ??= InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var hardwareRaw = await _settings.GetAsync(0, "hardware_acceleration", cancellationToken);
        var bufferRaw = await _settings.GetAsync(0, "buffer_ms", cancellationToken);
        var volumeRaw = await _settings.GetAsync(0, "volume", cancellationToken);
        _networkCachingMs = int.TryParse(bufferRaw, out var buffer) ? Math.Clamp(buffer, 200, 10000) : 1500;
        var hardware = hardwareRaw != "0";

        await Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc = new LibVLC(
                enableDebugLogs: false,
                hardware ? "--avcodec-hw=any" : "--avcodec-hw=none");

            // Forward LibVLC's own warnings/errors (HTTP status, TLS, demux) into the log so stream
            // failures are diagnosable instead of a silent EncounteredError.
            _libVlc.Log += (_, e) =>
            {
                if (e.Level >= LibVLCSharp.Shared.LogLevel.Warning)
                {
                    _logger.LogDebug("libvlc {Module}: {Message}", e.Module, e.Message);
                }
            };

            _player = new MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = hardware,
            };
        }, cancellationToken);

        var player = _player!;
        player.Volume = int.TryParse(volumeRaw, out var volume) ? Math.Clamp(volume, 0, 100) : Volume;
        _dispatcher.Invoke(() => Volume = player.Volume);

        player.Playing += (_, _) =>
        {
            _logger.LogDebug("libvlc event: Playing");
            OnUi(() =>
            {
                State = PlaybackState.Playing;
                ErrorMessage = null;
                ReconnectAttempt = 0;
                CancelReconnect();
                RefreshTracks();
                ApplyPendingResume();
                if (IsVod)
                {
                    _positionTimer.Start();
                }
            });
        };
        player.Paused += (_, _) => OnUi(() =>
        {
            State = PlaybackState.Paused;
            SaveVodProgress();
        });
        player.Buffering += (_, e) => OnUi(() =>
        {
            if (e.Cache < 100 && State is PlaybackState.Opening or PlaybackState.Playing or PlaybackState.Buffering)
            {
                State = PlaybackState.Buffering;
            }
            else if (e.Cache >= 100 && State == PlaybackState.Buffering)
            {
                State = PlaybackState.Playing;
            }
        });
        player.EncounteredError += (_, _) =>
        {
            _logger.LogDebug("libvlc event: EncounteredError");
            OnUi(() =>
            {
                if (IsVod)
                {
                    HandleVodError();
                }
                else
                {
                    HandleStreamFailure();
                }
            });
        };
        player.EndReached += (_, _) =>
        {
            _logger.LogDebug("libvlc event: EndReached");
            OnUi(() =>
            {
                if (IsVod)
                {
                    HandleVodEnded();
                }
                else
                {
                    // A live stream ending unexpectedly is a drop — reconnect.
                    HandleStreamFailure();
                }
            });
        };
        player.Stopped += (_, _) => _logger.LogDebug("libvlc event: Stopped");
        player.Opening += (_, _) => _logger.LogDebug("libvlc event: Opening");

        _videoView = new VideoView { MediaPlayer = player };
        if (_overlayContent is not null)
        {
            _videoView.Content = _overlayContent;
        }

        if (_surfaces.TryGetValue(_activeSurface, out var host))
        {
            AttachViewTo(host);
        }
        _logger.LogInformation(
            "LibVLC initialized (hardware={Hardware}, caching={CachingMs}ms)", hardware, _networkCachingMs);
    }

    /// <summary>On the first Playing event after a VOD start, seek to the resume position.</summary>
    private void ApplyPendingResume()
    {
        if (!IsVod || _resumeApplied || _pendingResumeSeconds <= 1)
        {
            _resumeApplied = true;
            return;
        }

        _resumeApplied = true;
        var target = _pendingResumeSeconds;
        var player = _player;
        if (player is not null)
        {
            Task.Run(() => player.Time = (long)(target * 1000));
        }
    }

    private void RefreshTracks()
    {
        var player = _player;
        if (player is null)
        {
            return;
        }

        var audio = player.AudioTrackDescription
            .Where(t => t.Id >= 0)
            .Select(t => new TrackOption(t.Id, t.Name ?? $"Track {t.Id}"))
            .ToList();
        var subtitles = player.SpuDescription
            .Select(t => new TrackOption(t.Id, t.Name ?? (t.Id < 0 ? "Off" : $"Subtitle {t.Id}")))
            .ToList();
        if (subtitles.Count == 0 || subtitles[0].Id >= 0)
        {
            subtitles.Insert(0, new TrackOption(-1, "Off"));
        }

        AudioTracks = audio;
        SubtitleTracks = subtitles;
        ActiveAudioTrack = player.AudioTrack;
        ActiveSubtitleTrack = player.Spu;
    }

    // ------------------------------------------------------------------ VOD end/error

    /// <summary>A movie/episode finished — persist a "completed" position and go idle.</summary>
    private void HandleVodEnded()
    {
        if (_userStopped)
        {
            return;
        }

        // Snap to the end so SaveVodProgress stores position 0 (won't nag to resume the credits).
        if (DurationSeconds > 0)
        {
            PositionSeconds = DurationSeconds;
        }

        SaveVodProgress();
        _positionTimer.Stop();
        State = PlaybackState.Idle;
    }

    /// <summary>A VOD stream errored — surface an inline error (no live-style reconnect loop).</summary>
    private void HandleVodError()
    {
        if (_userStopped)
        {
            return;
        }

        SaveVodProgress();
        _positionTimer.Stop();
        State = PlaybackState.Error;
        ErrorMessage = "This title could not be played. It may be unavailable or in an unsupported format.";
    }

    // ------------------------------------------------------------------ reconnect

    /// <summary>Live stream dropped (error or unexpected end). Retry with exponential backoff.</summary>
    private void HandleStreamFailure()
    {
        if (_userStopped || IsVod || _currentUrl is null || _currentChannel is null)
        {
            return;
        }

        if (State == PlaybackState.Reconnecting)
        {
            return; // an attempt is already scheduled; its failure path handles progression
        }

        _ = RunReconnectLoopAsync();
    }

    private async Task RunReconnectLoopAsync()
    {
        CancelReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        // Copy the token now: success cancels AND disposes the source from the Playing
        // handler, after which cts.Token would throw ObjectDisposedException.
        var cancelToken = cts.Token;
        var channel = _currentChannel!;
        var url = _currentUrl!;
        var sequence = Volatile.Read(ref _playRequestSequence);

        for (var attempt = 1; attempt <= ReconnectDelays.Length; attempt++)
        {
            if (cancelToken.IsCancellationRequested)
            {
                return; // playback recovered (or the user stopped/zapped) mid-loop
            }

            State = PlaybackState.Reconnecting;
            ReconnectAttempt = attempt;
            _logger.LogWarning(
                "Stream dropped for {Channel}; reconnect attempt {Attempt}/{Max}",
                channel.Name, attempt, ReconnectDelays.Length);

            try
            {
                await Task.Delay(ReconnectDelays[attempt - 1], cancelToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var played = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPlaying(object? s, EventArgs e) => played.TrySetResult(true);
            void OnFailed(object? s, EventArgs e) => played.TrySetResult(false);

            var player = _player!;
            player.Playing += OnPlaying;
            player.EncounteredError += OnFailed;
            player.EndReached += OnFailed;
            try
            {
                await StartPlaybackAsync(url, channel, sequence);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var succeeded = await played.Task.WaitAsync(timeout.Token);
                    if (succeeded)
                    {
                        return; // Playing event already restored state
                    }
                }
                catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // attempt timed out; fall through to the next attempt
                }
            }
            finally
            {
                player.Playing -= OnPlaying;
                player.EncounteredError -= OnFailed;
                player.EndReached -= OnFailed;
            }
        }

        State = PlaybackState.Error;
        ErrorMessage = $"Lost connection to “{channel.Name}” after {ReconnectDelays.Length} attempts.";
    }

    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The User-Agent to send for a stream: an explicit per-channel UA (from an M3U
    /// <c>#EXTVLCOPT</c>) wins, then the profile override, then the app default. Never empty — many
    /// panels 403 unrecognized agents, so a stream must always present a whitelisted one.
    /// </summary>
    private string EffectiveUserAgent(Channel? channel)
    {
        if (!string.IsNullOrEmpty(channel?.UserAgent))
        {
            return channel.UserAgent;
        }

        var profileUserAgent = _session.CurrentProfile?.StreamUserAgent;
        return string.IsNullOrWhiteSpace(profileUserAgent)
            ? Profile.DefaultStreamUserAgent
            : profileUserAgent;
    }

    private string? ResolveStreamUrl(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            return channel.StreamUrl;
        }

        var profile = _session.CurrentProfile;
        if (profile is null || channel.ProviderStreamId is null ||
            _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        var container = profile.PreferHls ? LiveStreamContainer.Hls : LiveStreamContainer.MpegTs;
        return XtreamUrls.Live(
            credentials.Server, credentials.Username, credentials.Password,
            channel.ProviderStreamId, container).AbsoluteUri;
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
