namespace Lumen.Core.Models;

/// <summary>A live TV capture: in progress (recording) or finished (playable offline).</summary>
public sealed class Recording
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>The source channel's row id; null once the channel vanishes on a resync.</summary>
    public long? ChannelId { get; set; }

    /// <summary>Channel name at record time (survives channel deletion/renames).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>EPG programme airing when the recording started; null without guide data.</summary>
    public string? ProgrammeTitle { get; set; }

    /// <summary>User-chosen display name; overrides the captured metadata when set.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>Channel logo at record time, for the Recordings page card.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Absolute path of the final .ts file (a growing capture uses a suffixed sibling).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Reuses <see cref="DownloadStatus"/>: <see cref="DownloadStatus.Downloading"/> means
    /// "recording"; <see cref="DownloadStatus.Queued"/> is reserved for future scheduled
    /// recordings; Paused is unused.
    /// </summary>
    public DownloadStatus Status { get; set; }

    /// <summary>Last error message when <see cref="Status"/> is <see cref="DownloadStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Recording start, unix seconds (UTC).</summary>
    public long StartedUtc { get; set; }

    /// <summary>Recording end, unix seconds (UTC); null while recording.</summary>
    public long? StoppedUtc { get; set; }

    /// <summary>Captured length in seconds, computed at finalize; null while recording.</summary>
    public long? DurationSeconds { get; set; }

    /// <summary>Final file size in bytes, set at finalize (0 while recording).</summary>
    public long SizeBytes { get; set; }
}
