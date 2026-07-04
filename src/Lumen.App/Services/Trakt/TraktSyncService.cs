using CommunityToolkit.Mvvm.Messaging;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>
/// The two-way Trakt engine. Pull: snapshot the account's watched history (gated by
/// last_activities) and mark matching local items watched. Push: send locally completed items
/// that Trakt doesn't know about, plus immediate history add/remove for manual toggles.
/// Episode reconciliation runs when a series' details are loaded — that's the only moment
/// season/episode numbers meet provider episode ids (there is no local episodes table).
/// </summary>
public sealed class TraktSyncService
{
    private const int CatalogPageSize = 2000;

    private readonly ITraktClient _client;
    private readonly TraktAuthStore _store;
    private readonly TraktMatchService _matcher;
    private readonly ITraktMatchRepository _matches;
    private readonly ITraktWatchedRepository _watched;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly ICatalogRepository _catalog;
    private readonly IProfileRepository _profiles;
    private readonly ISettingsRepository _settings;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;
    private readonly ILogger<TraktSyncService> _logger;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public TraktSyncService(
        ITraktClient client,
        TraktAuthStore store,
        TraktMatchService matcher,
        ITraktMatchRepository matches,
        ITraktWatchedRepository watched,
        IWatchHistoryRepository watchHistory,
        ICatalogRepository catalog,
        IProfileRepository profiles,
        ISettingsRepository settings,
        IClock clock,
        IMessenger messenger,
        ILogger<TraktSyncService> logger)
    {
        _client = client;
        _store = store;
        _matcher = matcher;
        _matches = matches;
        _watched = watched;
        _watchHistory = watchHistory;
        _catalog = catalog;
        _profiles = profiles;
        _settings = settings;
        _clock = clock;
        _messenger = messenger;
        _logger = logger;
    }

    /// <summary>True while a full sync runs (drives the settings page busy state).</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>Two-way sync is on unless explicitly disabled (a fresh connect should just work).</summary>
    public async Task<bool> IsSyncEnabledAsync(CancellationToken cancellationToken)
    {
        var value = await _settings.GetAsync(0, TraktSettingsKeys.SyncEnabled, cancellationToken).ConfigureAwait(false);
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await _store.IsConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a full sync: pull (when Trakt reports new activity, or <paramref name="force"/>),
    /// then per-profile movie reconciliation and history push. Single-flight — a call while a
    /// sync runs returns false immediately.
    /// </summary>
    public async Task<bool> SyncNowAsync(bool force, CancellationToken cancellationToken)
    {
        if (!await IsSyncEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        IsSyncing = true;
        try
        {
            var access = await _store.GetValidAccessAsync(cancellationToken).ConfigureAwait(false);
            if (access is null)
            {
                return false;
            }

            var activities = await _client.GetLastActivitiesAsync(access, cancellationToken).ConfigureAwait(false);
            var stamp = $"{activities?.Movies?.WatchedAt}|{activities?.Episodes?.WatchedAt}";
            var lastStamp = await _settings.GetAsync(0, TraktSettingsKeys.LastActivities, cancellationToken)
                .ConfigureAwait(false);

            if (force || !string.Equals(stamp, lastStamp, StringComparison.Ordinal))
            {
                await PullSnapshotAsync(access, cancellationToken).ConfigureAwait(false);
            }

            var profiles = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReconcileMoviesAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                await PushLocalCompletedAsync(profile.Id, access, cancellationToken).ConfigureAwait(false);
            }

            await _settings.SetAsync(0, TraktSettingsKeys.LastActivities, stamp, cancellationToken).ConfigureAwait(false);
            await _settings.SetAsync(
                0, TraktSettingsKeys.LastSyncUtc,
                _clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            _messenger.Send(new TraktSyncCompletedMessage());
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trakt sync failed; it will retry on the next cycle");
            return false;
        }
        finally
        {
            IsSyncing = false;
            _syncGate.Release();
        }
    }

    /// <summary>When the connected Trakt account last synced, unix seconds; 0 when never.</summary>
    public async Task<long> GetLastSyncUtcAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(0, TraktSettingsKeys.LastSyncUtc, cancellationToken).ConfigureAwait(false);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    // ------------------------------------------------------------------ pull + reconcile

    private async Task PullSnapshotAsync(TraktAccess access, CancellationToken cancellationToken)
    {
        var movies = await _client.GetWatchedMoviesAsync(access, cancellationToken).ConfigureAwait(false);
        var shows = await _client.GetWatchedShowsAsync(access, cancellationToken).ConfigureAwait(false);

        var items = new List<TraktWatchedItem>(movies.Count + shows.Count * 8);
        foreach (var movie in movies)
        {
            if (movie.Movie?.Ids?.Trakt is not { } traktId)
            {
                continue;
            }

            items.Add(new TraktWatchedItem
            {
                MediaType = TraktMediaType.Movie,
                TraktId = traktId,
                TmdbId = movie.Movie.Ids.Tmdb,
                ImdbId = movie.Movie.Ids.Imdb,
                Title = movie.Movie.Title ?? string.Empty,
                Year = movie.Movie.Year,
                Plays = Math.Max(1, movie.Plays),
                LastWatchedUtc = movie.LastWatchedAt?.ToUnixTimeSeconds() ?? 0,
            });
        }

        foreach (var show in shows)
        {
            if (show.Show?.Ids?.Trakt is not { } showTraktId)
            {
                continue;
            }

            foreach (var season in show.Seasons ?? [])
            {
                foreach (var episode in season.Episodes ?? [])
                {
                    items.Add(new TraktWatchedItem
                    {
                        MediaType = TraktMediaType.Episode,
                        TraktId = showTraktId,
                        TmdbId = show.Show.Ids.Tmdb,
                        ImdbId = show.Show.Ids.Imdb,
                        Title = show.Show.Title ?? string.Empty,
                        Year = show.Show.Year,
                        Season = season.Number,
                        EpisodeNumber = episode.Number,
                        Plays = Math.Max(1, episode.Plays),
                        LastWatchedUtc = episode.LastWatchedAt?.ToUnixTimeSeconds() ?? 0,
                    });
                }
            }
        }

        await _watched.ReplaceAllAsync(items, cancellationToken).ConfigureAwait(false);
        _matcher.InvalidateSnapshotIndex();
        _logger.LogInformation(
            "Trakt snapshot updated: {Movies} movies, {Episodes} episodes",
            items.Count(i => i.MediaType == TraktMediaType.Movie),
            items.Count(i => i.MediaType == TraktMediaType.Episode));
    }

    /// <summary>Marks catalog movies watched from the snapshot (id joins, then bulk title joins).</summary>
    private async Task ReconcileMoviesAsync(long profileId, CancellationToken cancellationToken)
    {
        var watchedMovies = await _watched.GetMoviesAsync(cancellationToken).ConfigureAwait(false);
        if (watchedMovies.Count == 0)
        {
            return;
        }

        var byTrakt = new Dictionary<long, TraktWatchedItem>();
        var byTmdb = new Dictionary<long, TraktWatchedItem>();
        var byImdb = new Dictionary<string, TraktWatchedItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in watchedMovies)
        {
            byTrakt.TryAdd(movie.TraktId, movie);
            if (movie.TmdbId is { } tmdb)
            {
                byTmdb.TryAdd(tmdb, movie);
            }

            if (!string.IsNullOrEmpty(movie.ImdbId))
            {
                byImdb.TryAdd(movie.ImdbId, movie);
            }
        }

        var matchesByKey = (await _matches.GetAllAsync(profileId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.ItemKind == ContentKind.Movie)
            .ToDictionary(m => m.ItemKey, StringComparer.Ordinal);

        var marked = 0;
        for (var offset = 0; ; offset += CatalogPageSize)
        {
            var page = await _catalog.GetVodItemsAsync(
                profileId, ContentKind.Movie, categoryId: null, search: null, VodSortOrder.Added,
                CatalogPageSize, offset, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            var localEntries = await _watchHistory.GetByKeysAsync(
                profileId, ContentKind.Movie, page.Select(i => i.ProviderItemId).ToList(), cancellationToken)
                .ConfigureAwait(false);
            var localByKey = localEntries.ToDictionary(e => e.ItemKey, StringComparer.Ordinal);

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                matchesByKey.TryGetValue(item.ProviderItemId, out var match);
                if (match is null || match.IsNegative)
                {
                    // A fresh snapshot may contain a title that failed to match before.
                    var joined = await _matcher.TryTitleJoinAsync(profileId, item, cancellationToken)
                        .ConfigureAwait(false);
                    if (joined is null)
                    {
                        continue;
                    }

                    await _matcher.StoreAsync(joined, cancellationToken).ConfigureAwait(false);
                    matchesByKey[item.ProviderItemId] = joined;
                    match = joined;
                }

                var watched = FindWatched(match, byTrakt, byTmdb, byImdb);
                if (watched is null)
                {
                    continue;
                }

                localByKey.TryGetValue(item.ProviderItemId, out var local);
                if (local is { Completed: true } && local.PlayCount >= watched.Plays)
                {
                    continue; // already reflects the snapshot
                }

                await _watchHistory.SetCompletedAsync(
                    new WatchHistoryEntry
                    {
                        ProfileId = profileId,
                        ItemKind = ContentKind.Movie,
                        ItemKey = item.ProviderItemId,
                        Title = item.Name,
                        PosterUrl = item.PosterUrl,
                        DurationSeconds = local?.DurationSeconds ?? 0,
                        WatchedUtc = watched.LastWatchedUtc,
                        PlayCount = watched.Plays,
                        CompletedUtc = watched.LastWatchedUtc,
                    },
                    completed: true, cancellationToken).ConfigureAwait(false);
                marked++;
            }

            if (page.Count < CatalogPageSize)
            {
                break;
            }
        }

        if (marked > 0)
        {
            _logger.LogInformation("Trakt reconcile marked {Count} movie(s) watched for profile {Profile}", marked, profileId);
        }
    }

    /// <summary>
    /// Marks a loaded series' episodes watched from the snapshot. Returns how many episodes
    /// changed so the detail page knows to refresh its rows.
    /// </summary>
    public async Task<int> ReconcileSeriesAsync(
        long profileId, VodItem series, SeriesDetails details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(details);
        if (!await IsSyncEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var match = await _matcher.ResolveAsync(
            profileId, series, details.TmdbId, details.ImdbId, allowSearch: true, cancellationToken)
            .ConfigureAwait(false);
        if (match?.TraktId is not { } showTraktId)
        {
            return 0; // not matched, or the show isn't in the watched snapshot
        }

        var watchedEpisodes = await _watched.GetEpisodesForShowAsync(showTraktId, cancellationToken).ConfigureAwait(false);
        if (watchedEpisodes.Count == 0)
        {
            return 0;
        }

        var watchedByNumber = watchedEpisodes.ToDictionary(e => (e.Season, e.EpisodeNumber));
        var localByKey = (await _watchHistory.GetForSeriesAsync(profileId, series.ProviderItemId, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.ItemKey, StringComparer.Ordinal);

        var changed = 0;
        foreach (var episode in details.Seasons.SelectMany(s => s.Episodes))
        {
            if (!watchedByNumber.TryGetValue((episode.Season, episode.Number), out var watched))
            {
                continue;
            }

            var key = $"{series.ProviderItemId}:{episode.ProviderEpisodeId}";
            localByKey.TryGetValue(key, out var local);
            if (local is { Completed: true } && local.PlayCount >= watched.Plays)
            {
                continue;
            }

            await _watchHistory.SetCompletedAsync(
                new WatchHistoryEntry
                {
                    ProfileId = profileId,
                    ItemKind = ContentKind.Series,
                    ItemKey = key,
                    Title = $"{series.Name} · S{episode.Season}E{episode.Number}",
                    PosterUrl = series.PosterUrl,
                    DurationSeconds = local?.DurationSeconds ?? episode.DurationSeconds ?? 0,
                    WatchedUtc = watched.LastWatchedUtc,
                    PlayCount = watched.Plays,
                    CompletedUtc = watched.LastWatchedUtc,
                    Season = episode.Season,
                    EpisodeNumber = episode.Number,
                },
                completed: true, cancellationToken).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    /// <summary>Marks a movie watched locally when the snapshot says so. True when state changed.</summary>
    public async Task<bool> ReconcileMovieAsync(
        long profileId, VodItem item, MovieDetails? details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!await IsSyncEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var match = await _matcher.ResolveAsync(
            profileId, item, details?.TmdbId, details?.ImdbId, allowSearch: true, cancellationToken)
            .ConfigureAwait(false);
        if (match is null)
        {
            return false;
        }

        var movies = await _watched.GetMoviesAsync(cancellationToken).ConfigureAwait(false);
        var watched = movies.FirstOrDefault(m =>
            (match.TraktId is { } trakt && m.TraktId == trakt)
            || (match.TmdbId is { } tmdb && m.TmdbId == tmdb)
            || (match.ImdbId is { } imdb && string.Equals(m.ImdbId, imdb, StringComparison.OrdinalIgnoreCase)));
        if (watched is null)
        {
            return false;
        }

        var local = await _watchHistory.GetAsync(profileId, ContentKind.Movie, item.ProviderItemId, cancellationToken)
            .ConfigureAwait(false);
        if (local is { Completed: true } && local.PlayCount >= watched.Plays)
        {
            return false;
        }

        await _watchHistory.SetCompletedAsync(
            new WatchHistoryEntry
            {
                ProfileId = profileId,
                ItemKind = ContentKind.Movie,
                ItemKey = item.ProviderItemId,
                Title = item.Name,
                PosterUrl = item.PosterUrl,
                DurationSeconds = local?.DurationSeconds ?? details?.DurationSeconds ?? 0,
                WatchedUtc = watched.LastWatchedUtc,
                PlayCount = watched.Plays,
                CompletedUtc = watched.LastWatchedUtc,
            },
            completed: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ push

    /// <summary>Sends locally completed items the snapshot doesn't contain to Trakt history.</summary>
    private async Task PushLocalCompletedAsync(long profileId, TraktAccess access, CancellationToken cancellationToken)
    {
        var completed = await _watchHistory.GetCompletedAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (completed.Count == 0)
        {
            return;
        }

        var snapshot = await _watched.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var movieTrakt = new HashSet<long>();
        var movieTmdb = new HashSet<long>();
        var movieImdb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var episodeByTrakt = new HashSet<(long, int, int)>();
        var episodeByTmdb = new HashSet<(long, int, int)>();
        foreach (var item in snapshot)
        {
            if (item.MediaType == TraktMediaType.Movie)
            {
                movieTrakt.Add(item.TraktId);
                if (item.TmdbId is { } tmdb)
                {
                    movieTmdb.Add(tmdb);
                }

                if (!string.IsNullOrEmpty(item.ImdbId))
                {
                    movieImdb.Add(item.ImdbId);
                }
            }
            else
            {
                episodeByTrakt.Add((item.TraktId, item.Season, item.EpisodeNumber));
                if (item.TmdbId is { } tmdb)
                {
                    episodeByTmdb.Add((tmdb, item.Season, item.EpisodeNumber));
                }
            }
        }

        var matchesByKey = (await _matches.GetAllAsync(profileId, cancellationToken).ConfigureAwait(false))
            .Where(m => !m.IsNegative)
            .ToDictionary(m => (m.ItemKind, m.ItemKey), m => m);

        var movies = new List<TraktHistoryMovie>();
        var showGroups = new Dictionary<string, TraktHistoryShow>(StringComparer.Ordinal);

        foreach (var entry in completed)
        {
            if (entry.ItemKind == ContentKind.Movie)
            {
                if (!matchesByKey.TryGetValue((ContentKind.Movie, entry.ItemKey), out var match))
                {
                    continue;
                }

                var known = (match.TraktId is { } trakt && movieTrakt.Contains(trakt))
                    || (match.TmdbId is { } tmdb && movieTmdb.Contains(tmdb))
                    || (match.ImdbId is { } imdb && movieImdb.Contains(imdb));
                if (known)
                {
                    continue;
                }

                movies.Add(new TraktHistoryMovie
                {
                    WatchedAt = DateTimeOffset.FromUnixTimeSeconds(entry.CompletedUtc ?? entry.WatchedUtc),
                    Ids = ToIds(match),
                });
            }
            else if (entry.ItemKind == ContentKind.Series
                && entry.Season is { } season && entry.EpisodeNumber is { } number)
            {
                var separator = entry.ItemKey.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0
                    || !matchesByKey.TryGetValue((ContentKind.Series, entry.ItemKey[..separator]), out var match))
                {
                    continue;
                }

                var known = (match.TraktId is { } trakt && episodeByTrakt.Contains((trakt, season, number)))
                    || (match.TmdbId is { } tmdb && episodeByTmdb.Contains((tmdb, season, number)));
                if (known)
                {
                    continue;
                }

                var showKey = entry.ItemKey[..separator];
                if (!showGroups.TryGetValue(showKey, out var show))
                {
                    show = new TraktHistoryShow { Ids = ToIds(match), Seasons = [] };
                    showGroups[showKey] = show;
                }

                var seasonGroup = show.Seasons!.FirstOrDefault(s => s.Number == season);
                if (seasonGroup is null)
                {
                    seasonGroup = new TraktHistorySeason { Number = season, Episodes = [] };
                    show.Seasons!.Add(seasonGroup);
                }

                seasonGroup.Episodes!.Add(new TraktHistoryEpisode
                {
                    Number = number,
                    WatchedAt = DateTimeOffset.FromUnixTimeSeconds(entry.CompletedUtc ?? entry.WatchedUtc),
                });
            }
        }

        if (movies.Count == 0 && showGroups.Count == 0)
        {
            return;
        }

        await _client.AddToHistoryAsync(
            access,
            new TraktHistoryRequest
            {
                Movies = movies.Count > 0 ? movies : null,
                Shows = showGroups.Count > 0 ? [.. showGroups.Values] : null,
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Pushed {Movies} movie(s) and {Shows} show(s) to Trakt history for profile {Profile}",
            movies.Count, showGroups.Count, profileId);
    }

    /// <summary>
    /// Immediate push for a manual watched toggle. Unwatching removes the item's plays from
    /// Trakt history and drops it from the local snapshot so the next reconcile can't
    /// resurrect it before a fresh pull.
    /// </summary>
    public async Task PushManualToggleAsync(
        long profileId,
        VodItem item,
        WatchHistoryEntry entry,
        bool watched,
        long? detailsTmdbId,
        string? detailsImdbId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(entry);
        if (!await IsSyncEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var access = await _store.GetValidAccessAsync(cancellationToken).ConfigureAwait(false);
            if (access is null)
            {
                return;
            }

            var match = await _matcher.ResolveAsync(
                profileId, item, detailsTmdbId, detailsImdbId, allowSearch: true, cancellationToken)
                .ConfigureAwait(false);
            if (match is null)
            {
                return;
            }

            var request = BuildSingleItemRequest(match, entry);
            if (request is null)
            {
                return;
            }

            if (watched)
            {
                await _client.AddToHistoryAsync(access, request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.RemoveFromHistoryAsync(access, request, cancellationToken).ConfigureAwait(false);
                if (match.TraktId is { } traktId)
                {
                    if (entry.ItemKind == ContentKind.Movie)
                    {
                        await _watched.DeleteAsync(TraktMediaType.Movie, traktId, null, null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (entry.Season is { } season && entry.EpisodeNumber is { } number)
                    {
                        await _watched.DeleteAsync(TraktMediaType.Episode, traktId, season, number, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (TraktApiException ex)
        {
            _logger.LogWarning(ex, "Pushing a manual watched toggle to Trakt failed (local state is kept)");
        }
    }

    private static TraktHistoryRequest? BuildSingleItemRequest(TraktMatch match, WatchHistoryEntry entry)
    {
        var watchedAt = DateTimeOffset.FromUnixTimeSeconds(
            entry.CompletedUtc ?? (entry.WatchedUtc > 0 ? entry.WatchedUtc : DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (entry.ItemKind == ContentKind.Movie)
        {
            return new TraktHistoryRequest
            {
                Movies = [new TraktHistoryMovie { WatchedAt = watchedAt, Ids = ToIds(match) }],
            };
        }

        if (entry.Season is not { } season || entry.EpisodeNumber is not { } number)
        {
            return null;
        }

        return new TraktHistoryRequest
        {
            Shows =
            [
                new TraktHistoryShow
                {
                    Ids = ToIds(match),
                    Seasons =
                    [
                        new TraktHistorySeason
                        {
                            Number = season,
                            Episodes = [new TraktHistoryEpisode { Number = number, WatchedAt = watchedAt }],
                        },
                    ],
                },
            ],
        };
    }

    private static TraktWatchedItem? FindWatched(
        TraktMatch match,
        Dictionary<long, TraktWatchedItem> byTrakt,
        Dictionary<long, TraktWatchedItem> byTmdb,
        Dictionary<string, TraktWatchedItem> byImdb)
    {
        if (match.TraktId is { } trakt && byTrakt.TryGetValue(trakt, out var hit))
        {
            return hit;
        }

        if (match.TmdbId is { } tmdb && byTmdb.TryGetValue(tmdb, out hit))
        {
            return hit;
        }

        return match.ImdbId is { } imdb && byImdb.TryGetValue(imdb, out hit) ? hit : null;
    }

    private static TraktIds ToIds(TraktMatch match) => new()
    {
        Trakt = match.TraktId,
        Tmdb = match.TmdbId,
        Imdb = match.ImdbId,
    };
}
