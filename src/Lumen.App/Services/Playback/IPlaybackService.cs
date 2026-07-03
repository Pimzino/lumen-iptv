using LibVLCSharp.Shared;
using Lumen.Core.Models;

namespace Lumen.App.Services.Playback;

/// <summary>Player lifecycle states surfaced to the UI.</summary>
public enum PlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Reconnecting,
    Error,
}

/// <summary>Named video surfaces the shared VideoView can attach to.</summary>
public enum VideoSurfaceKind
{
    Preview,
    FullPlayer,
    MiniPlayer,
}

/// <summary>How the full player is left.</summary>
public enum PlayerExitMode
{
    /// <summary>Hide the player overlay and return to browsing; playback continues (preview surface).</summary>
    Browse,

    /// <summary>Collapse into the floating picture-in-picture window.</summary>
    MiniPlayer,

    /// <summary>Stop playback entirely.</summary>
    Stop,
}

/// <summary>An audio or subtitle track choice.</summary>
public sealed record TrackOption(int Id, string Name);

/// <summary>A VOD play request carrying resume + identity metadata for watch history.</summary>
public sealed record VodPlayRequest(
    string Url,
    ContentKind Kind,
    string ItemKey,
    string Title,
    string? PosterUrl,
    double ResumeSeconds);

/// <summary>Video aspect handling modes cycled by the player overlay.</summary>
public enum AspectMode
{
    Fit,
    Ratio16X9,
    Ratio4X3,
    Fill,
}

/// <summary>
/// Owns the single LibVLC MediaPlayer for the whole app. Views host the shared video
/// surface; switching preview ↔ full player ↔ mini player never recreates the player.
/// </summary>
public interface IPlaybackService
{
    PlaybackState State { get; }

    Channel? CurrentChannel { get; }

    string? ErrorMessage { get; }

    /// <summary>Current reconnect attempt (1-based) while <see cref="PlaybackState.Reconnecting"/>.</summary>
    int ReconnectAttempt { get; }

    int Volume { get; set; }

    bool IsMuted { get; set; }

    AspectMode Aspect { get; }

    IReadOnlyList<TrackOption> AudioTracks { get; }

    IReadOnlyList<TrackOption> SubtitleTracks { get; }

    int ActiveAudioTrack { get; }

    int ActiveSubtitleTrack { get; }

    bool IsFullPlayerActive { get; }

    bool IsMiniPlayerActive { get; }

    /// <summary>The underlying player, once initialized (for the video surface).</summary>
    MediaPlayer? Player { get; }

    /// <summary>
    /// Plays a live channel. <paramref name="zapList"/> supplies the ↑/↓ zap order;
    /// <paramref name="preview"/> plays muted without affecting the user's mute choice.
    /// </summary>
    Task PlayChannelAsync(Channel channel, IReadOnlyList<Channel>? zapList, bool preview, CancellationToken cancellationToken);

    Task ZapAsync(int direction);

    void TogglePause();

    void Stop();

    void SelectAudioTrack(int id);

    void SelectSubtitleTrack(int id);

    void CycleAspect();

    /// <summary>Enters the edge-to-edge player (unmutes preview audio).</summary>
    void EnterFullPlayer();

    /// <summary>Leaves the full player: back to browsing, into the mini player, or stopped.</summary>
    void ExitFullPlayer(PlayerExitMode mode);

    /// <summary>Registers a host decorator for a surface slot.</summary>
    void RegisterSurface(VideoSurfaceKind kind, System.Windows.Controls.Decorator host);

    void UnregisterSurface(VideoSurfaceKind kind, System.Windows.Controls.Decorator host);
}
