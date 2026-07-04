namespace Lumen.Core.Models;

/// <summary>How a provider item was matched to its Trakt identity.</summary>
public enum TraktMatchMethod
{
    /// <summary>The provider supplied a TMDB/IMDB id directly (Xtream detail endpoints).</summary>
    ProviderId = 0,

    /// <summary>Joined locally by normalized title + year against the Trakt watched snapshot.</summary>
    TitleJoin = 1,

    /// <summary>Resolved via the Trakt text-search API.</summary>
    Search = 2,

    Manual = 3,
}

/// <summary>
/// A provider item's resolved Trakt/TMDB identity, per profile. A row with all ids null is a
/// negative match: the lookup ran and found nothing; it is retried after a cooldown and flushed
/// when Trakt credentials change.
/// </summary>
public sealed class TraktMatch
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary><see cref="ContentKind.Movie"/> or <see cref="ContentKind.Series"/> (series-level, not per episode).</summary>
    public ContentKind ItemKind { get; set; }

    /// <summary>The provider item id (<c>VodItem.ProviderItemId</c>).</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Trakt id of the movie, or of the show for series.</summary>
    public long? TraktId { get; set; }

    public long? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    /// <summary>Canonical Trakt title/year the item matched to (diagnostics and dedupe).</summary>
    public string? MatchedTitle { get; set; }

    public int? MatchedYear { get; set; }

    public TraktMatchMethod Method { get; set; }

    /// <summary>Match time, unix seconds (UTC). Negative entries expire and retry.</summary>
    public long MatchedUtc { get; set; }

    /// <summary>True when the lookup found nothing (no ids).</summary>
    public bool IsNegative => TraktId is null && TmdbId is null && ImdbId is null;
}
