namespace Lumen.Core.Models;

/// <summary>A live TV channel belonging to a profile.</summary>
public sealed class Channel
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    public long? CategoryId { get; set; }

    /// <summary>Xtream stream_id (null for M3U channels).</summary>
    public string? ProviderStreamId { get; set; }

    /// <summary>Channel number when the provider supplies one.</summary>
    public int? Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    /// <summary>Direct stream URL (M3U channels). Xtream channels build URLs from credentials.</summary>
    public string? StreamUrl { get; set; }

    /// <summary>EPG channel hint from the playlist (tvg-id) or Xtream epg_channel_id.</summary>
    public string? EpgChannelId { get; set; }

    /// <summary>EPG time shift in minutes (tvg-shift).</summary>
    public int TvgShiftMinutes { get; set; }

    /// <summary>HTTP User-Agent override for playback (#EXTVLCOPT).</summary>
    public string? UserAgent { get; set; }

    /// <summary>HTTP Referrer override for playback (#EXTVLCOPT).</summary>
    public string? Referrer { get; set; }

    /// <summary>True when the provider archives this channel for catch-up (Xtream tv_archive).</summary>
    public bool HasArchive { get; set; }

    /// <summary>Days the catch-up archive reaches back (0 = unknown).</summary>
    public int ArchiveDays { get; set; }

    public bool IsHidden { get; set; }

    /// <summary>Import time, unix seconds (UTC).</summary>
    public long AddedUtc { get; set; }
}
