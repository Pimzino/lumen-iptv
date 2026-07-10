namespace Lumen.Core.Models;

/// <summary>A movie or series episode downloaded (or downloading) for offline viewing.</summary>
public sealed class DownloadItem
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary><see cref="ContentKind.Movie"/> or <see cref="ContentKind.Series"/> (an episode).</summary>
    public ContentKind Kind { get; set; }

    /// <summary>
    /// The playback watch-history key, identical to the one used online: the movie's
    /// <see cref="VodItem.ProviderItemId"/>, or <c>"{seriesProviderId}:{episodeId}"</c> for an
    /// episode. Reusing it means resume/watched/Trakt carry across online↔offline.
    /// </summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>The series' provider id for an episode (groups the Downloads page); null for movies.</summary>
    public string? SeriesItemKey { get; set; }

    /// <summary>Provider id used to rebuild the stream URL at download time (movie stream id / episode id).</summary>
    public string ProviderItemId { get; set; } = string.Empty;

    /// <summary>Container extension reported by the provider (mp4/mkv/…); null for M3U-direct items.</summary>
    public string? ContainerExtension { get; set; }

    /// <summary>Direct stream URL for M3U-sourced VOD (never a credentialed Xtream URL); null otherwise.</summary>
    public string? StreamUrl { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? PosterUrl { get; set; }

    /// <summary>Season number for an episode; null for movies.</summary>
    public int? Season { get; set; }

    /// <summary>Episode number within its season; null for movies.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// True when the stream is HLS (.m3u8) and was captured via LibVLC stream-output rather than a
    /// direct HTTP download. Chosen at enqueue; may be corrected by the progressive→HLS fallback.
    /// </summary>
    public bool IsHls { get; set; }

    /// <summary>Absolute path of the final local file (a growing partial uses a suffixed sibling).</summary>
    public string FilePath { get; set; } = string.Empty;

    public DownloadStatus Status { get; set; }

    /// <summary>Last error message when <see cref="Status"/> is <see cref="DownloadStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Total size in bytes when known (progressive with a Content-Length); null otherwise.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Bytes written so far (progressive); the durable resume source is the partial file itself.</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// Uniform progress in per-mille (0–1000), set by both strategies so the UI reads one fraction:
    /// progressive derives it from bytes, HLS from playback time over duration.
    /// </summary>
    public int ProgressPermille { get; set; }

    /// <summary>Enqueue time, unix seconds (UTC).</summary>
    public long CreatedUtc { get; set; }

    /// <summary>Completion time, unix seconds (UTC); null until <see cref="DownloadStatus.Completed"/>.</summary>
    public long? CompletedUtc { get; set; }
}
