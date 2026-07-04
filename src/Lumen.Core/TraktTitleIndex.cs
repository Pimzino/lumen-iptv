using Lumen.Core.Models;

namespace Lumen.Core;

/// <summary>
/// Lookup structure over the Trakt watched snapshot: by TMDB id, by IMDB id, and by folded
/// title (see <see cref="NameNormalizer"/>). Episode rows collapse to one identity entry per
/// show. Drives the zero-API-call title join from messy provider names to Trakt identities.
/// </summary>
public sealed class TraktTitleIndex
{
    private readonly Dictionary<(TraktMediaType Type, long Tmdb), TraktWatchedItem> _byTmdb = [];
    private readonly Dictionary<(TraktMediaType Type, string Imdb), TraktWatchedItem> _byImdb = [];
    private readonly Dictionary<(TraktMediaType Type, string Title), List<TraktWatchedItem>> _byTitle = [];

    private TraktTitleIndex()
    {
    }

    public static TraktTitleIndex Build(IReadOnlyList<TraktWatchedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var index = new TraktTitleIndex();
        var seenShows = new HashSet<long>();
        foreach (var item in items)
        {
            if (item.MediaType == TraktMediaType.Episode && !seenShows.Add(item.TraktId))
            {
                continue; // one identity entry per show
            }

            if (item.TmdbId is { } tmdb)
            {
                index._byTmdb.TryAdd((item.MediaType, tmdb), item);
            }

            if (!string.IsNullOrEmpty(item.ImdbId))
            {
                index._byImdb.TryAdd((item.MediaType, item.ImdbId), item);
            }

            var folded = NameNormalizer.Normalize(item.Title);
            if (folded.Length > 0)
            {
                if (!index._byTitle.TryGetValue((item.MediaType, folded), out var list))
                {
                    list = [];
                    index._byTitle[(item.MediaType, folded)] = list;
                }

                list.Add(item);
            }
        }

        return index;
    }

    /// <summary>
    /// Unique folded-title match with year tolerance ±1. Several distinct candidates (remakes,
    /// same-name shows) return null — an ambiguous join is worse than none.
    /// </summary>
    public TraktWatchedItem? Find(TraktMediaType type, string foldedTitle, int? year)
    {
        if (string.IsNullOrEmpty(foldedTitle) || !_byTitle.TryGetValue((type, foldedTitle), out var candidates))
        {
            return null;
        }

        var narrowed = year is { } y
            ? candidates.Where(c => c.Year is null || Math.Abs(c.Year.Value - y) <= 1).ToList()
            : candidates;
        var distinct = narrowed.DistinctBy(c => c.TraktId).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>Fills a provider-id match's missing ids (notably the trakt id) from the snapshot.</summary>
    public void Enrich(TraktMatch match, TraktMediaType type)
    {
        ArgumentNullException.ThrowIfNull(match);
        TraktWatchedItem? hit = null;
        if (match.TmdbId is { } tmdb)
        {
            _byTmdb.TryGetValue((type, tmdb), out hit);
        }

        if (hit is null && match.ImdbId is { } imdb)
        {
            _byImdb.TryGetValue((type, imdb), out hit);
        }

        if (hit is not null)
        {
            match.TraktId ??= hit.TraktId;
            match.TmdbId ??= hit.TmdbId;
            match.ImdbId ??= hit.ImdbId;
        }
    }
}
