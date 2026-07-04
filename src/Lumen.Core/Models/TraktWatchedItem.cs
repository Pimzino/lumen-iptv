namespace Lumen.Core.Models;

/// <summary>Media kinds in the Trakt watched snapshot.</summary>
public enum TraktMediaType
{
    Movie = 0,
    Episode = 1,
}

/// <summary>
/// One watched movie or episode from the connected Trakt account (app-global snapshot,
/// replaced on each pull). Episodes carry the show's Trakt identity plus season/number;
/// movies use season/episode 0.
/// </summary>
public sealed class TraktWatchedItem
{
    public long Id { get; set; }

    public TraktMediaType MediaType { get; set; }

    /// <summary>Trakt id of the movie, or of the show for episodes.</summary>
    public long TraktId { get; set; }

    public long? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    /// <summary>Movie or show title as Trakt knows it (drives the local title join).</summary>
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int Season { get; set; }

    public int EpisodeNumber { get; set; }

    public int Plays { get; set; }

    /// <summary>Last watch time on Trakt, unix seconds (UTC).</summary>
    public long LastWatchedUtc { get; set; }
}
