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
    private readonly IXtreamClientFactory _xtreamFactory;
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

    // Completion is credited once per play session (saves fire on every pause/stop after 95%,
    // and the play count must not grow with each one).
    private bool _completedRecordedThisPlay;

    // Watch-history writes run on pool threads; shutdown drains the last one (FlushProgressAsync)
    // so a pause-then-quit doesn't lose the resume position.
    private Task _lastProgressSave = Task.CompletedTask;

    // Live watch tracking: a channel lands in watch history only after this much real
    // (non-preview) playback, so zapping through channels doesn't pollute "Recently watched".
    private const int LiveWatchThresholdSeconds = 10;
    private int _liveWatchSeconds;
    private long _liveWatchRecordedChannelId = -1;

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

    /// <summary>Last reported LibVLC cache fill (0–100) while <see cref="PlaybackState.Buffering"/>.</summary>
    [ObservableProperty]
    private double _bufferingProgress;

    /// <summary>
    /// True when live playback is time-shifted behind the broadcast — pausing live TV resumes
    /// where it left off, not at the live edge. Drives the "Go to Live" affordance.
    /// </summary>
    [ObservableProperty]
    private bool _isBehindLive;

    /// <summary>Seconds the playhead lags the live broadcast (0 at the live edge, and for VOD).</summary>
    [ObservableProperty]
    private double _liveDelaySeconds;

    /// <summary>True when the current live channel supports catch-up seeking (provider archive).</summary>
    [ObservableProperty]
    private bool _canSeekLive;

    /// <summary>True while the current channel plays from the provider archive rather than the live feed.</summary>
    [ObservableProperty]
    private bool _isTimeshift;

    /// <summary>Accrued shift below this is imperceptible next to normal IPTV latency — don't surface it.</summary>
    private const double BehindLiveThresholdSeconds = 3;

    /// <summary>Live seeks landing this close to now just rejoin the live stream.</summary>
    private const double LiveEdgeSnapSeconds = 60;

    // Live time-shift tracking: when the current pause started, and the shift completed pauses
    // have already banked. Reset whenever a stream (re)opens at the live edge.
    private DateTimeOffset? _livePausedAtUtc;
    private double _liveShiftSeconds;

    // Timeshift (catch-up) stream state: the absolute broadcast time the current archive
    // stream starts at, and how far into it playback has advanced (player.Time cache).
    private DateTimeOffset? _timeshiftBaseUtc;
    private double _timeshiftPlayedSeconds;
    private DateTimeOffset? _lastTimeshiftEndUtc;

    // Panel clock info for timeshift start times, cached per profile.
    private long _serverInfoProfileId = -1;
    private XtreamServerInfo? _serverInfo;

    public PlaybackService(
        ISessionService session,
        ISettingsRepository settings,
        IWatchHistoryRepository watchHistory,
        IClock clock,
        IMessenger messenger,
        IXtreamClientFactory xtreamFactory,
        ILogger<PlaybackService> logger)
    {
        _session = session;
        _settings = settings;
        _watchHistory = watchHistory;
        _clock = clock;
        _messenger = messenger;
        _xtreamFactory = xtreamFactory;
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

    /// <summary>The VOD request currently loaded; null during live playback. Observed by the Trakt scrobbler.</summary>
    public VodPlayRequest? CurrentVod
    {
        get => _currentVod;
        private set => SetProperty(ref _currentVod, value);
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

        // Loading feedback first: flip to Opening before (possibly cold) LibVLC init so the
        // spinner covers the whole open, not just the tail after native init.
        ErrorMessage = null;
        ReconnectAttempt = 0;
        BufferingProgress = 0;
        State = PlaybackState.Opening;

        if (!await TryInitializeAsync(cancellationToken))
        {
            return;
        }

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
        CurrentVod = null;
        IsVod = false;
        PositionSeconds = 0;
        DurationSeconds = 0;
        ResetBehindLive();

        _liveWatchSeconds = 0;
        _liveWatchRecordedChannelId = -1;

        _currentChannel = channel;
        OnPropertyChanged(nameof(CurrentChannel));
        NotifyNowPlayingChanged();
        CanSeekLive = channel.HasArchive
            && channel.ProviderStreamId is not null
            && _session.CurrentProfile is { } archiveProfile
            && _session.GetXtreamCredentials(archiveProfile) is not null;
        if (zapList is not null && !ReferenceEquals(zapList, _zapList))
        {
            _zapList = zapList;
            OnPropertyChanged(nameof(ZapList));
        }
        _isPreviewContext = preview;
        _currentUrl = url;

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

        // Feedback first: enter the full player in the Opening state before any awaits, so the
        // click lands on a spinner immediately instead of a dead detail page while (possibly
        // cold) LibVLC init and the stream open run.
        ErrorMessage = null;
        ReconnectAttempt = 0;
        BufferingProgress = 0;
        State = PlaybackState.Opening;
        EnterFullPlayer();

        if (!await TryInitializeAsync(cancellationToken))
        {
            return;
        }

        CancelReconnect();
        _userStopped = false;

        // A previous VOD may still be playing (kept alive as a browse preview) — bank its position.
        SaveVodProgress();

        _currentChannel = null;
        OnPropertyChanged(nameof(CurrentChannel));
        CanSeekLive = false;
        _liveWatchSeconds = 0;
        _liveWatchRecordedChannelId = -1;

        // Null-then-set: records compare by value, so replaying the same title (Start over twice)
        // would otherwise raise no change — observers (the Trakt scrobbler) key sessions off it.
        CurrentVod = null;
        CurrentVod = request;
        NotifyNowPlayingChanged();
        _isPreviewContext = false;
        _currentUrl = request.Url;
        _pendingResumeSeconds = request.ResumeSeconds;
        _resumeApplied = false;
        _completedRecordedThisPlay = false;
        IsVod = true;
        PositionSeconds = request.ResumeSeconds;
        DurationSeconds = 0;
        ResetBehindLive();

        ApplyMute();
        await StartVodAsync(request, sequence);
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

    /// <summary>
    /// Re-opens the current live channel at the live edge, discarding the pause time-shift.
    /// IPTV servers stream from "now" on connect and LibVLC can't seek forward in a live
    /// HTTP/TS feed, so a fresh open of the same URL is the reliable jump back to live.
    /// </summary>
    public async Task GoToLiveAsync()
    {
        var channel = _currentChannel;
        var url = _currentUrl;
        if (IsVod || channel is null || url is null || _player is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _playRequestSequence);
        CancelReconnect();
        _userStopped = false;
        ErrorMessage = null;
        ReconnectAttempt = 0;
        BufferingProgress = 0;
        ResetBehindLive();
        ApplyMute();
        State = PlaybackState.Opening;
        await StartPlaybackAsync(url, channel, sequence);
    }

    /// <summary>
    /// Jumps live playback to an absolute broadcast time by streaming from the provider's
    /// catch-up archive (<see cref="CanSeekLive"/>). Targets within a minute of now rejoin
    /// the live stream instead. Scrubbing issues a fresh archive request per seek — the
    /// archived stream itself is not reliably seekable, but a new start time always is.
    /// </summary>
    public async Task SeekLiveAsync(DateTimeOffset targetUtc)
    {
        var channel = _currentChannel;
        var profile = _session.CurrentProfile;
        if (IsVod || !CanSeekLive || channel?.ProviderStreamId is null || profile is null || _player is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        if ((now - targetUtc).TotalSeconds < LiveEdgeSnapSeconds)
        {
            await GoToLiveAsync();
            return;
        }

        // Don't reach past the provider's advertised archive window.
        if (channel.ArchiveDays > 0 && targetUtc < now.AddDays(-channel.ArchiveDays))
        {
            targetUtc = now.AddDays(-channel.ArchiveDays);
        }

        if (_session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return;
        }

        // Timeshift start times are written in the panel's local clock — resolve it once
        // per profile. Abort (leaving playback untouched) if the panel can't be reached.
        var serverInfo = await GetServerInfoAsync(profile.Id, credentials);
        if (serverInfo is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _playRequestSequence);
        CancelReconnect();
        _userStopped = false;
        ErrorMessage = null;
        ReconnectAttempt = 0;
        BufferingProgress = 0;

        _livePausedAtUtc = null;
        _liveShiftSeconds = 0;
        _timeshiftBaseUtc = targetUtc;
        _timeshiftPlayedSeconds = 0;
        IsTimeshift = true;
        UpdateBehindLive();

        ApplyMute();
        State = PlaybackState.Opening;
        var url = BuildTimeshiftUrl(credentials, channel.ProviderStreamId, targetUtc, now, serverInfo);
        await StartPlaybackAsync(url, channel, sequence);
    }

    private static string BuildTimeshiftUrl(
        XtreamCredentials credentials,
        string streamId,
        DateTimeOffset startUtc,
        DateTimeOffset nowUtc,
        XtreamServerInfo serverInfo)
    {
        // Cover from the seek point up to the live edge, plus slack so the stream keeps
        // serving as the archive grows underneath it.
        var durationMinutes = Math.Max(1, (int)Math.Ceiling((nowUtc - startUtc).TotalMinutes) + 60);
        var serverLocalStart = XtreamServerTime.ToServerLocal(startUtc, serverInfo);
        return XtreamUrls.Timeshift(
            credentials.Server, credentials.Username, credentials.Password,
            streamId, serverLocalStart, durationMinutes).AbsoluteUri;
    }

    /// <summary>Panel server info (timezone/clock), cached per profile after the first fetch.</summary>
    private async Task<XtreamServerInfo?> GetServerInfoAsync(long profileId, XtreamCredentials credentials)
    {
        if (_serverInfoProfileId == profileId && _serverInfo is not null)
        {
            return _serverInfo;
        }

        try
        {
            var response = await _xtreamFactory.Create(credentials).AuthenticateAsync(CancellationToken.None);

            // A panel that reports no server_info still gets timeshift — start times fall
            // back to UTC, which is also what such panels overwhelmingly run on.
            _serverInfo = response.ServerInfo ?? new XtreamServerInfo();
            _serverInfoProfileId = profileId;
            return _serverInfo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live seek aborted: fetching Xtream server info failed");
            return null;
        }
    }

    public void Stop()
    {
        _userStopped = true;
        CancelReconnect();
        Interlocked.Increment(ref _playRequestSequence);

        // Persist the VOD resume position before tearing down.
        SaveVodProgress();
        _positionTimer.Stop();
        CurrentVod = null;
        IsVod = false;
        ResetBehindLive();

        _currentChannel = null;
        OnPropertyChanged(nameof(CurrentChannel));
        NotifyNowPlayingChanged();
        CanSeekLive = false;
        _liveWatchSeconds = 0;
        _liveWatchRecordedChannelId = -1;
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
        if (player is null)
        {
            return;
        }

        if (!IsVod)
        {
            if (IsTimeshift && State is PlaybackState.Playing or PlaybackState.Buffering)
            {
                _timeshiftPlayedSeconds = Math.Max(0, player.Time / 1000.0);
            }

            TrackLiveWatch();
            UpdateBehindLive(); // the time-shift keeps growing while a live stream sits paused
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

    /// <summary>
    /// Recomputes <see cref="LiveDelaySeconds"/> and <see cref="IsBehindLive"/>. Timeshifted
    /// playback derives its delay from the archive start plus play progress; edge playback
    /// derives it from banked pauses plus any pause in progress.
    /// </summary>
    private void UpdateBehindLive()
    {
        double delay;
        if (IsTimeshift && _timeshiftBaseUtc is { } baseUtc)
        {
            delay = Math.Max(0, (_clock.UtcNow - baseUtc).TotalSeconds - _timeshiftPlayedSeconds);
        }
        else
        {
            delay = _liveShiftSeconds;
            if (_livePausedAtUtc is { } pausedAt)
            {
                delay += (_clock.UtcNow - pausedAt).TotalSeconds;
            }
        }

        LiveDelaySeconds = IsVod ? 0 : delay;
        IsBehindLive = !IsVod
            && delay >= BehindLiveThresholdSeconds
            && State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;
    }

    private void ResetBehindLive()
    {
        _livePausedAtUtc = null;
        _liveShiftSeconds = 0;
        _timeshiftBaseUtc = null;
        _timeshiftPlayedSeconds = 0;
        _lastTimeshiftEndUtc = null;
        IsTimeshift = false;
        UpdateBehindLive();
    }

    /// <summary>
    /// Counts real live viewing (playing, not the muted browse preview) and records the channel
    /// into watch history once it crosses <see cref="LiveWatchThresholdSeconds"/> — at most once
    /// per channel play, so reconnects and pause cycles don't rewrite the entry.
    /// </summary>
    private void TrackLiveWatch()
    {
        if (State != PlaybackState.Playing || _isPreviewContext
            || _currentChannel is not { } channel || channel.Id == _liveWatchRecordedChannelId)
        {
            return;
        }

        if (++_liveWatchSeconds < LiveWatchThresholdSeconds || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        _liveWatchRecordedChannelId = channel.Id;
        var entry = new WatchHistoryEntry
        {
            ProfileId = profile.Id,
            ItemKind = ContentKind.Live,
            ItemKey = channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = channel.Name,
            PosterUrl = channel.LogoUrl,
            PositionSeconds = 0,
            DurationSeconds = 0,
            WatchedUtc = _clock.UtcNow.ToUnixTimeSeconds(),
        };
        _lastProgressSave = _watchHistory.UpsertAsync(entry, CancellationToken.None);
    }

    /// <summary>Writes the current VOD position to watch history (throttled to meaningful progress).</summary>
    private void SaveVodProgress()
    {
        if (_currentVod is not { } vod || _session.CurrentProfile is not { } profile || DurationSeconds <= 0)
        {
            return;
        }

        // Treat "finished" (>95%) as position 0 so it doesn't nag to resume the last seconds;
        // the completed flag (merged, never regressed by later saves) keeps the watched state.
        var fraction = PositionSeconds / DurationSeconds;
        var storedPosition = fraction is > 0.95 or < 0.02 ? 0 : PositionSeconds;
        var finished = fraction > 0.95;
        var creditPlay = finished && !_completedRecordedThisPlay;
        _completedRecordedThisPlay |= finished;

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
            Completed = finished,
            PlayCount = creditPlay ? 1 : 0,
            CompletedUtc = creditPlay ? _clock.UtcNow.ToUnixTimeSeconds() : null,
            Season = vod.Season,
            EpisodeNumber = vod.EpisodeNumber,
        };
        _lastProgressSave = _watchHistory.UpsertAsync(entry, CancellationToken.None);
        _messenger.Send(new WatchProgressSavedMessage(entry));
    }

    /// <summary>The most recent watch-history write, awaited at shutdown.</summary>
    public Task FlushProgressAsync() => _lastProgressSave;

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

        // Attach the video surface one Background beat later: moving the shared view rebuilds
        // the native HWND and its overlay airspace, none of which can render this frame anyway.
        // Deferring lets the player layer (background + loading caption) paint first, so the
        // click lands on instant feedback instead of a black pane.
        _dispatcher.InvokeAsync(
            () =>
            {
                if (IsFullPlayerActive)
                {
                    ActivateSurface(VideoSurfaceKind.FullPlayer);
                }
            },
            DispatcherPriority.Background);
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
                // Handing the video to the muted browse preview is a live-TV affordance: the Live
                // TV list is the only page with a preview surface, so a live channel keeps playing
                // there behind the list. A VOD has nowhere to preview into — keeping it alive just
                // leaves the movie decoding off-screen until it resurfaces in the next preview pane
                // the user opens (the muted Live TV preview would show the leftover film). Backing
                // out of a movie ends it instead; Stop banks the resume position, and the dedicated
                // PiP button remains the way to keep a VOD playing while browsing.
                //
                // A live channel has the same problem when playback was started from somewhere other
                // than the Live TV list (Search, Home, Favorites, the Guide): no preview surface is
                // registered, so handing off would orphan the video on the now-hidden full-player
                // surface — it keeps decoding unseen and its opening/buffering overlay leaks onto
                // whatever page is revealed. Stop in that case too.
                if (IsVod || !_surfaces.ContainsKey(VideoSurfaceKind.Preview))
                {
                    Stop();
                    return;
                }

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

        UpdateVideoAttachment();
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

        UpdateVideoAttachment();
    }

    /// <summary>
    /// True while the shared video view is parented to a surface slot — from then on the
    /// in-video overlay (LibVLCSharp's foreground window, always above the native HWND) owns
    /// all loading feedback. False only before LibVLC cold init or when no slot hosts the view.
    /// </summary>
    private bool _videoViewAttached;

    /// <summary>
    /// True when a stream is opening/buffering and the in-video overlay is <b>not</b> on
    /// screen to say so. Pages bind their fallback loading blocks to exactly this, so the
    /// user never sees two stacked loading indicators — the video host window is a
    /// transparent Win32 child that does not hide WPF content behind it, so a state-only
    /// check would show the fallback *through* the video pane while the overlay is also up.
    /// </summary>
    public bool IsColdOpenLoading =>
        State is PlaybackState.Opening or PlaybackState.Buffering && !_videoViewAttached;

    private void UpdateVideoAttachment()
    {
        var attached = _videoView?.Parent is not null;
        if (attached != _videoViewAttached)
        {
            _videoViewAttached = attached;
            OnPropertyChanged(nameof(IsColdOpenLoading));
        }
    }

    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(IsColdOpenLoading));
        UpdateBehindLive(); // behind-live only surfaces in watchable states (not Error/Idle/Opening)
    }

    // ------------------------------------------------------------------ init & events

    private int _networkCachingMs = 1500;
    private readonly object _initGate = new();
    private Task? _initTask;

    /// <summary>
    /// Pre-initializes LibVLC so the first real play skips the cold native init.
    /// Called fire-and-forget at startup; failures are logged and retried on the next play.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playback warm-up failed; init will retry on first play");
        }
    }

    /// <summary>
    /// <see cref="EnsureInitializedAsync"/>, but a failure surfaces as the Error playback state
    /// instead of leaving the caller's Opening spinner up forever.
    /// </summary>
    private async Task<bool> TryInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LibVLC initialization failed");
            State = PlaybackState.Error;
            ErrorMessage = "The video engine failed to start. Try again, or check the logs.";
            return false;
        }
    }

    /// <summary>
    /// Initializes LibVLC + the MediaPlayer exactly once. Concurrent first-play requests share
    /// one init Task rather than each racing to build (and leak) a second player.
    /// </summary>
    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_initGate)
        {
            if (_initTask is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _initTask = null; // a failed init (e.g. warm-up racing startup) retries next time
            }

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
                hardware ? "--avcodec-hw=any" : "--avcodec-hw=none",
                // IPTV joins streams mid-GOP and TS discontinuities are routine; libvlc's
                // default is to *render* frames the decoder knows are broken (gray smears,
                // macroblock soup) rather than drop them. Hold the last clean frame instead.
                "--no-avcodec-corrupted");

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
                if (!IsVod)
                {
                    if (!IsTimeshift && State == PlaybackState.Paused && _livePausedAtUtc is { } pausedAt)
                    {
                        // Resuming a paused live stream continues time-shifted, not at the edge.
                        _liveShiftSeconds += (_clock.UtcNow - pausedAt).TotalSeconds;
                    }
                    else if (State is PlaybackState.Opening or PlaybackState.Reconnecting)
                    {
                        // A fresh open joins the live edge — unless it's an archive (timeshift)
                        // stream, whose lag is derived from its base time instead.
                        _liveShiftSeconds = 0;
                    }

                    _livePausedAtUtc = null;
                }

                State = PlaybackState.Playing;
                ErrorMessage = null;
                ReconnectAttempt = 0;
                CancelReconnect();

                // LibVLC can bring the audio output back muted or with a stale session after a
                // pause/underrun cycle — re-assert the user's audio state on every (re)start.
                // Only the explicit channel-open path did this before, which is why resume,
                // reconnect, and go-to-live could stay silent until a full channel reopen.
                // Order matters: setting Volume implicitly unmutes some audio outputs, so the
                // mute state (which keeps browse previews silent) must be asserted LAST.
                player.Volume = Math.Clamp(Volume, 0, 100);
                ApplyMute();

                RefreshTracks();
                ApplyPendingResume();

                // Ticks drive the VOD position *and* the live behind-the-broadcast counter.
                _positionTimer.Start();
            });
        };
        player.Paused += (_, _) => OnUi(() =>
        {
            if (!IsVod && !IsTimeshift)
            {
                // Edge playback: the shift starts accruing. (Timeshift lag needs no clock —
                // player.Time freezes while paused, which grows the derived delay.)
                _livePausedAtUtc = _clock.UtcNow;
            }

            State = PlaybackState.Paused;
            SaveVodProgress();
        });
        player.Buffering += (_, e) => OnUi(() =>
        {
            BufferingProgress = e.Cache;
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
                else if (IsTimeshift)
                {
                    HandleTimeshiftEnded();
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

        // The VideoView is a WPF control (HwndHost) — always build it on the dispatcher, whatever
        // thread the initiating play/warm-up continuation happens to run on.
        _dispatcher.Invoke(() =>
        {
            _videoView = new VideoView { MediaPlayer = player };
            if (_overlayContent is not null)
            {
                _videoView.Content = _overlayContent;
            }

            // Loaded fires after every (re)attach, once layout has built the native host
            // window — the earliest reliable moment to blacken it (idempotent thereafter).
            _videoView.Loaded += (_, _) =>
            {
                if (_videoView is not null)
                {
                    var applied = VideoHostBlackout.Apply(_videoView);
                    _logger.LogDebug(
                        "Video surface loaded; host blackout {Status}",
                        applied ? "active" : "pending (host window not built yet)");
                }
            };

            if (_surfaces.TryGetValue(_activeSurface, out var host))
            {
                AttachViewTo(host);
            }

            UpdateVideoAttachment();
        });
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

    /// <summary>
    /// A timeshift stream ran off the end of its requested window (or the panel closed it).
    /// Keep the replay going from the current playhead, or rejoin live when close to the
    /// edge. Back-to-back immediate ends mean the archive isn't actually serving — fail over
    /// to the normal reconnect path (at the live edge) instead of looping on the archive.
    /// </summary>
    private void HandleTimeshiftEnded()
    {
        if (_userStopped)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (_lastTimeshiftEndUtc is { } last && (now - last).TotalSeconds < 15)
        {
            _logger.LogWarning("Timeshift stream ended twice in quick succession; falling back to live");
            ResetBehindLive();
            HandleStreamFailure();
            return;
        }

        _lastTimeshiftEndUtc = now;
        var playhead = now.AddSeconds(-LiveDelaySeconds);
        if (LiveDelaySeconds < 180)
        {
            _ = GoToLiveAsync();
        }
        else
        {
            _ = SeekLiveAsync(playhead);
        }
    }

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
        var url = ResolveReconnectUrl(channel);
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

    /// <summary>
    /// The URL reconnect attempts should replay. A dropped timeshift stream resumes the
    /// replay from the playhead where it failed (fresh archive request); anything else —
    /// including a timeshift whose prerequisites vanished — rejoins the live stream.
    /// </summary>
    private string ResolveReconnectUrl(Channel channel)
    {
        if (IsTimeshift
            && channel.ProviderStreamId is { } streamId
            && _serverInfo is { } serverInfo
            && _session.CurrentProfile is { } profile
            && _session.GetXtreamCredentials(profile) is { } credentials)
        {
            var now = _clock.UtcNow;
            var playhead = now.AddSeconds(-LiveDelaySeconds);
            _timeshiftBaseUtc = playhead;
            _timeshiftPlayedSeconds = 0;
            return BuildTimeshiftUrl(credentials, streamId, playhead, now, serverInfo);
        }

        if (IsTimeshift)
        {
            ResetBehindLive();
        }

        return _currentUrl!;
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
