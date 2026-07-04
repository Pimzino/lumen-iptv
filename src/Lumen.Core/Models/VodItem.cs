namespace Lumen.Core.Models;

/// <summary>A movie or series entry in the local catalog cache.</summary>
public sealed class VodItem
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary><see cref="ContentKind.Movie"/> or <see cref="ContentKind.Series"/>.</summary>
    public ContentKind Kind { get; set; }

    /// <summary>Provider-side id (Xtream stream_id / series_id, or URL hash for M3U VOD).</summary>
    public string ProviderItemId { get; set; } = string.Empty;

    public long? CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? PosterUrl { get; set; }

    /// <summary>Provider rating on a 0–10 scale when available.</summary>
    public double? Rating { get; set; }

    public int? Year { get; set; }

    /// <summary>Provider "added" timestamp, unix seconds (UTC).</summary>
    public long? ProviderAddedUtc { get; set; }

    /// <summary>Container extension for movies (mp4/mkv); null for series.</summary>
    public string? ContainerExtension { get; set; }

    /// <summary>Direct stream URL for M3U-sourced VOD; null for Xtream items.</summary>
    public string? StreamUrl { get; set; }

    /// <summary>
    /// Total episodes across all seasons, cached when the series' details load; null until
    /// then (and always null for movies). Drives the grid's series watched-fraction bar.
    /// </summary>
    public int? EpisodeTotal { get; set; }
}
