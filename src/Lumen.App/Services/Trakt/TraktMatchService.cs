using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>
/// Resolves messy provider items ("EN| The.Matrix.(1999) [4K]") to Trakt identities, cheapest
/// first: provider-supplied TMDB/IMDB ids, then a zero-API-call title join against the watched
/// snapshot, then the Trakt search API. Results (including "found nothing") are cached in
/// <c>trakt_match</c>; negatives retry after a cooldown.
/// </summary>
public sealed class TraktMatchService
{
    private const int NegativeRetrySeconds = 7 * 24 * 3600;

    private readonly ITraktMatchRepository _matches;
    private readonly ITraktWatchedRepository _watched;
    private readonly TraktAuthStore _store;
    private readonly ITraktClient _client;
    private readonly IClock _clock;
    private readonly ILogger<TraktMatchService> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    private volatile TraktTitleIndex? _index;

    public TraktMatchService(
        ITraktMatchRepository matches,
        ITraktWatchedRepository watched,
        TraktAuthStore store,
        ITraktClient client,
        IClock clock,
        ILogger<TraktMatchService> logger)
    {
        _matches = matches;
        _watched = watched;
        _store = store;
        _client = client;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Drops the in-memory snapshot index; called after each watched-history pull.</summary>
    public void InvalidateSnapshotIndex() => _index = null;

    /// <summary>
    /// Resolves an item's Trakt identity, creating or refreshing its match row. Provider ids
    /// (from Xtream detail responses) override any cached negative. Returns null when nothing
    /// matched (a negative row is stored so the search isn't hammered).
    /// </summary>
    public async Task<TraktMatch?> ResolveAsync(
        long profileId,
        VodItem item,
        long? providerTmdbId,
        string? providerImdbId,
        bool allowSearch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existing = await _matches.GetAsync(profileId, item.Kind, item.ProviderItemId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is { IsNegative: false })
        {
            return existing;
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var imdbId = string.IsNullOrWhiteSpace(providerImdbId) ? null : providerImdbId.Trim();
        var hasProviderIds = providerTmdbId is not null || imdbId is not null;
        if (existing is { IsNegative: true } && !hasProviderIds && now - existing.MatchedUtc < NegativeRetrySeconds)
        {
            return null;
        }

        // 1) Provider-supplied ids are authoritative; enrich with the snapshot's trakt id when known.
        if (hasProviderIds)
        {
            var match = new TraktMatch
            {
                ProfileId = profileId,
                ItemKind = item.Kind,
                ItemKey = item.ProviderItemId,
                TmdbId = providerTmdbId,
                ImdbId = imdbId,
                MatchedTitle = item.Name,
                MatchedYear = item.Year,
                Method = TraktMatchMethod.ProviderId,
                MatchedUtc = now,
            };
            var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            index.Enrich(match, MediaTypeOf(item.Kind));
            await _matches.UpsertAsync(match, cancellationToken).ConfigureAwait(false);
            return match;
        }

        // 2) Local title join against the watched snapshot (no API calls).
        var joined = await TryTitleJoinAsync(profileId, item, cancellationToken).ConfigureAwait(false);
        if (joined is not null)
        {
            await _matches.UpsertAsync(joined, cancellationToken).ConfigureAwait(false);
            return joined;
        }

        // 3) Trakt text search (needs a connection).
        if (allowSearch && await _store.GetValidAccessAsync(cancellationToken).ConfigureAwait(false) is { } access)
        {
            var found = await SearchAsync(profileId, item, access, now, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                await _matches.UpsertAsync(found, cancellationToken).ConfigureAwait(false);
                return found;
            }

            // Only a completed search stores a negative — a mere snapshot miss shouldn't
            // suppress a future search for a week.
            var negative = new TraktMatch
            {
                ProfileId = profileId,
                ItemKind = item.Kind,
                ItemKey = item.ProviderItemId,
                MatchedTitle = item.Name,
                MatchedYear = item.Year,
                Method = TraktMatchMethod.Search,
                MatchedUtc = now,
            };
            await _matches.UpsertAsync(negative, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Zero-API-call match against the watched snapshot: normalized cleaned title + year (±1).
    /// Ambiguous folds (several distinct candidates) are skipped. The caller persists the row —
    /// bulk reconcile uses this without writing negatives for the whole catalog.
    /// </summary>
    public async Task<TraktMatch?> TryTitleJoinAsync(long profileId, VodItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var clean = TitleCleaner.Clean(item.Name);
        var folded = NameNormalizer.Normalize(clean.Title);
        if (folded.Length == 0)
        {
            return null;
        }

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var candidate = index.Find(MediaTypeOf(item.Kind), folded, clean.Year ?? item.Year);
        if (candidate is null)
        {
            return null;
        }

        return new TraktMatch
        {
            ProfileId = profileId,
            ItemKind = item.Kind,
            ItemKey = item.ProviderItemId,
            TraktId = candidate.TraktId,
            TmdbId = candidate.TmdbId,
            ImdbId = candidate.ImdbId,
            MatchedTitle = candidate.Title,
            MatchedYear = candidate.Year,
            Method = TraktMatchMethod.TitleJoin,
            MatchedUtc = _clock.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>Persists a title-join match produced by <see cref="TryTitleJoinAsync"/>.</summary>
    public Task StoreAsync(TraktMatch match, CancellationToken cancellationToken) =>
        _matches.UpsertAsync(match, cancellationToken);

    private async Task<TraktMatch?> SearchAsync(
        long profileId, VodItem item, TraktAccess access, long now, CancellationToken cancellationToken)
    {
        var clean = TitleCleaner.Clean(item.Name);
        var query = clean.Title.Length > 0 ? clean.Title : item.Name;
        var type = item.Kind == ContentKind.Movie ? "movie" : "show";
        try
        {
            var results = await _client.SearchAsync(
                access, type, query, clean.Year ?? item.Year, cancellationToken).ConfigureAwait(false);
            var best = results.FirstOrDefault(r =>
                item.Kind == ContentKind.Movie ? r.Movie?.Ids is not null : r.Show?.Ids is not null);
            if (best is null)
            {
                return null;
            }

            var (title, year, ids) = item.Kind == ContentKind.Movie
                ? (best.Movie!.Title, best.Movie.Year, best.Movie.Ids!)
                : (best.Show!.Title, best.Show.Year, best.Show.Ids!);
            return new TraktMatch
            {
                ProfileId = profileId,
                ItemKind = item.Kind,
                ItemKey = item.ProviderItemId,
                TraktId = ids.Trakt,
                TmdbId = ids.Tmdb,
                ImdbId = ids.Imdb,
                MatchedTitle = title,
                MatchedYear = year,
                Method = TraktMatchMethod.Search,
                MatchedUtc = now,
            };
        }
        catch (TraktApiException ex)
        {
            _logger.LogDebug(ex, "Trakt search for {Title} failed", item.Name);
            return null;
        }
    }

    private static TraktMediaType MediaTypeOf(ContentKind kind) =>
        kind == ContentKind.Movie ? TraktMediaType.Movie : TraktMediaType.Episode;

    private async Task<TraktTitleIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is { } ready)
        {
            return ready;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is { } built)
            {
                return built;
            }

            var items = await _watched.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var index = TraktTitleIndex.Build(items);
            _index = index;
            return index;
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
