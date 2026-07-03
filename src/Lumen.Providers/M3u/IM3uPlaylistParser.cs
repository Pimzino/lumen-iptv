namespace Lumen.Providers.M3u;

/// <summary>One #EXTINF entry (or bare URL) from an M3U playlist.</summary>
public sealed record M3uEntry
{
    public required string Title { get; init; }

    public required string Url { get; init; }

    public string? TvgId { get; init; }

    public string? TvgName { get; init; }

    public string? LogoUrl { get; init; }

    public string? GroupTitle { get; init; }

    /// <summary>EPG shift in minutes (tvg-shift is expressed in hours, fractional allowed).</summary>
    public int TvgShiftMinutes { get; init; }

    public string? CatchupType { get; init; }

    /// <summary>HTTP User-Agent from #EXTVLCOPT:http-user-agent.</summary>
    public string? UserAgent { get; init; }

    /// <summary>HTTP Referrer from #EXTVLCOPT:http-referrer.</summary>
    public string? Referrer { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>
/// Streaming M3U/M3U8 playlist parser. Never materializes the playlist in memory —
/// 100MB+ files parse as a forward-only line stream.
/// </summary>
public interface IM3uPlaylistParser
{
    IAsyncEnumerable<M3uEntry> ParseAsync(Stream stream, CancellationToken cancellationToken = default);
}
