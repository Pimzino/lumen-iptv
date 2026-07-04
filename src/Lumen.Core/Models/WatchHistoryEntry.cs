namespace Lumen.Core.Models;

/// <summary>A watch-history record, including the resume position for VOD content.</summary>
public sealed class WatchHistoryEntry
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    public ContentKind ItemKind { get; set; }

    /// <summary>Stable key: channel row id for live, provider item id for VOD/series episodes.</summary>
    public string ItemKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? PosterUrl { get; set; }

    /// <summary>Playback position in seconds (0 for live content).</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Media duration in seconds when known.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Last watch time, unix seconds (UTC).</summary>
    public long WatchedUtc { get; set; }

    /// <summary>True once the item has been watched to completion (locally, manually, or via Trakt).</summary>
    public bool Completed { get; set; }

    /// <summary>On reads: total recorded plays. On upserts: a delta added to the stored count.</summary>
    public int PlayCount { get; set; }

    /// <summary>When the item was last watched to completion, unix seconds (UTC); null when never.</summary>
    public long? CompletedUtc { get; set; }

    /// <summary>Season number for series episode rows; null otherwise.</summary>
    public int? Season { get; set; }

    /// <summary>Episode number for series episode rows; null otherwise.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Fraction watched, clamped to [0, 1]; 0 when duration is unknown.</summary>
    public double Progress => DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) : 0;
}
