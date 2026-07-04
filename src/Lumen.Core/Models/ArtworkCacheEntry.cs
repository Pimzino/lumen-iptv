namespace Lumen.Core.Models;

/// <summary>
/// A resolved (or resolved-empty) external artwork lookup, keyed by content kind + cleaned
/// title + year so every profile and refresh reuses it. A row with null urls is a negative
/// cache entry: the lookup ran and found nothing.
/// </summary>
public sealed class ArtworkCacheEntry
{
    public ContentKind Kind { get; set; }

    /// <summary>Case-folded cleaned title (see <c>TitleCleaner</c>).</summary>
    public string TitleKey { get; set; } = string.Empty;

    /// <summary>Release year hint; 0 when unknown.</summary>
    public int Year { get; set; }

    public string? PosterUrl { get; set; }

    public string? BackdropUrl { get; set; }

    /// <summary>Which provider answered (tmdb/itunes/tvmaze); null for negative entries.</summary>
    public string? Provider { get; set; }

    /// <summary>Lookup time, unix seconds (UTC). Negative entries expire and retry.</summary>
    public long ResolvedUtc { get; set; }
}
