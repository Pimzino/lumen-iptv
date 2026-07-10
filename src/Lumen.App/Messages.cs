using Lumen.Core.Models;

namespace Lumen.App;

/// <summary>Sent after the active profile changes; pages reload their data.</summary>
public sealed record ProfileSwitchedMessage(Profile Profile);

/// <summary>Sent after an EPG import completes for the active profile.</summary>
public sealed record EpgRefreshedMessage(long ProfileId, long Programmes);

/// <summary>Sent when playback moves to a different channel.</summary>
public sealed record ChannelChangedMessage(Channel Channel);

/// <summary>Sent after a catalog (channels/VOD) sync completes for the active profile.</summary>
public sealed record CatalogRefreshedMessage(long ProfileId);

/// <summary>Sent when onboarding finishes; the shell restores its chrome and goes Home.</summary>
public sealed record OnboardingCompletedMessage(Profile Profile);

/// <summary>Sent when the app needs onboarding (no profiles); the shell hides its chrome.</summary>
public sealed record OnboardingRequiredMessage;

/// <summary>Sent when a page asks for the add-profile flow (shell owns navigation).</summary>
public sealed record AddProfileRequestedMessage;

/// <summary>Sent after a Trakt sync completes; open pages refresh their watched indicators.</summary>
public sealed record TraktSyncCompletedMessage;

/// <summary>
/// Sent whenever playback persists VOD progress (pause, stop, natural end, switching titles).
/// The player is an overlay, not a navigation — pages underneath listen and update the
/// affected row/card instead of waiting for a reload. The entry is the write payload:
/// its Completed flag can set a watched tick but never clears one (the store merges).
/// </summary>
public sealed record WatchProgressSavedMessage(WatchHistoryEntry Entry);

/// <summary>
/// Sent on every download status transition (queued → downloading → completed/failed/paused).
/// The Downloads page and the detail-page download buttons listen and update the affected row
/// without re-querying.
/// </summary>
public sealed record DownloadStateChangedMessage(long DownloadId, string ItemKey, DownloadStatus Status);

/// <summary>Sent when a download finishes; drives the optional completion toast.</summary>
public sealed record DownloadCompletedMessage(DownloadItem Item);

/// <summary>Sent when a download is removed; detail-page buttons reset to their "Download" state.</summary>
public sealed record DownloadRemovedMessage(long DownloadId, string ItemKey);

/// <summary>
/// Sent on every live-recording transition (started, finalized, failed, removed). The Recordings
/// page re-buckets its lists; the player overlay's Record toggle reflects the active capture.
/// </summary>
public sealed record RecordingStateChangedMessage(long RecordingId, DownloadStatus Status);
