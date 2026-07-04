using Lumen.Core.Models;

namespace Lumen.Core.Abstractions;

/// <summary>Sort order for VOD grids.</summary>
public enum VodSortOrder
{
    Added = 0,
    Name = 1,
    Rating = 2,
}

/// <summary>The current and next programme for a channel.</summary>
public sealed record NowNext(Programme? Now, Programme? Next);

/// <summary>Disk/image cache statistics for the settings page.</summary>
public sealed record CacheStats(long TotalBytes, int FileCount);

/// <summary>
/// A series' aggregated watch progress: completed episodes count 1 unit each, in-progress
/// episodes count their watched fraction. Divide by the series' episode total for a bar.
/// </summary>
public sealed record SeriesWatchSummary(double WatchedUnits, int CompletedCount);

/// <summary>CRUD over configured profiles.</summary>
public interface IProfileRepository
{
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken);

    Task<Profile?> GetAsync(long id, CancellationToken cancellationToken);

    Task<long> InsertAsync(Profile profile, CancellationToken cancellationToken);

    Task UpdateAsync(Profile profile, CancellationToken cancellationToken);

    Task DeleteAsync(long id, CancellationToken cancellationToken);

    Task TouchLastUsedAsync(long id, long unixSeconds, CancellationToken cancellationToken);
}

/// <summary>Categories, channels, and cached VOD items for a profile.</summary>
public interface ICatalogRepository
{
    Task<IReadOnlyList<Category>> GetCategoriesAsync(long profileId, ContentKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronizes categories of a kind with a provider snapshot. Existing rows are matched by
    /// provider_category_id so row ids (and favorites pointing at them) stay stable.
    /// </summary>
    Task ReplaceCategoriesAsync(long profileId, ContentKind kind, IReadOnlyList<Category> categories, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronizes channels with a provider snapshot. Matched by provider_stream_id (Xtream)
    /// or stream_url (M3U); unmatched local rows are deleted.
    /// </summary>
    Task UpsertChannelsAsync(long profileId, IReadOnlyList<Channel> channels, CancellationToken cancellationToken);

    Task<IReadOnlyList<Channel>> GetChannelsAsync(long profileId, long? categoryId, CancellationToken cancellationToken);

    Task<Channel?> GetChannelAsync(long id, CancellationToken cancellationToken);

    Task<int> CountChannelsAsync(long profileId, CancellationToken cancellationToken);

    Task UpsertVodItemsAsync(long profileId, ContentKind kind, IReadOnlyList<VodItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// Pages VOD items; <paramref name="search"/> optionally narrows by case-insensitive
    /// name substring.
    /// </summary>
    Task<IReadOnlyList<VodItem>> GetVodItemsAsync(
        long profileId,
        ContentKind kind,
        long? categoryId,
        string? search,
        VodSortOrder sort,
        int limit,
        int offset,
        CancellationToken cancellationToken);

    Task<VodItem?> GetVodItemAsync(long id, CancellationToken cancellationToken);

    /// <summary>Direct lookup by the provider's item id (unique per profile + kind).</summary>
    Task<VodItem?> GetVodItemByProviderIdAsync(
        long profileId, ContentKind kind, string providerItemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VodItem>> GetRecentVodAsync(long profileId, ContentKind kind, int limit, CancellationToken cancellationToken);

    Task SetCategoryKindOverrideAsync(long categoryId, ContentKind? kind, CancellationToken cancellationToken);

    /// <summary>Caches a series' total episode count (learned from its detail response).</summary>
    Task SetSeriesEpisodeTotalAsync(long vodItemId, int episodeTotal, CancellationToken cancellationToken);
}

/// <summary>EPG channels, programmes, and channel↔EPG mappings.</summary>
public interface IEpgRepository
{
    /// <summary>Now/next lookup for many channels at once (single query).</summary>
    Task<IReadOnlyDictionary<string, NowNext>> GetNowNextAsync(
        long profileId, IReadOnlyCollection<string> xmltvIds, long nowUnix, CancellationToken cancellationToken);

    /// <summary>Programmes overlapping [fromUnix, toUnix) for the given channels, ordered by channel then start.</summary>
    Task<IReadOnlyList<Programme>> GetProgrammesAsync(
        long profileId, IReadOnlyCollection<string> xmltvIds, long fromUnix, long toUnix, CancellationToken cancellationToken);

    Task<IReadOnlyList<EpgChannel>> GetEpgChannelsAsync(long profileId, CancellationToken cancellationToken);

    Task<int> PurgeProgrammesBeforeAsync(long profileId, long cutoffUnix, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelEpgMapping>> GetMappingsAsync(long profileId, CancellationToken cancellationToken);

    /// <summary>Sets or clears a manual mapping; manual mappings survive automatic remapping.</summary>
    Task SetManualMappingAsync(long channelId, string? xmltvId, CancellationToken cancellationToken);

    /// <summary>Re-runs the automatic matcher for all non-manual channels. Returns mapped count.</summary>
    Task<int> ApplyAutoMappingsAsync(long profileId, CancellationToken cancellationToken);

    Task<(long Channels, long Programmes)> GetCountsAsync(long profileId, CancellationToken cancellationToken);
}

/// <summary>Favorited channels and VOD items.</summary>
public interface IFavoritesRepository
{
    Task<IReadOnlyList<FavoriteItem>> GetAllAsync(long profileId, CancellationToken cancellationToken);

    Task AddAsync(long profileId, ContentKind kind, string itemKey, long unixSeconds, CancellationToken cancellationToken);

    Task RemoveAsync(long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken);
}

/// <summary>Watch history with VOD resume positions and watched/completion state.</summary>
public interface IWatchHistoryRepository
{
    /// <summary>
    /// Inserts or merges an entry. Position, duration, title, and watched time replace the stored
    /// values; <c>Completed</c> never regresses, <c>PlayCount</c> is added as a delta, and
    /// <c>CompletedUtc</c>/<c>Season</c>/<c>EpisodeNumber</c> only overwrite when non-null.
    /// </summary>
    Task UpsertAsync(WatchHistoryEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Marks an item watched or unwatched without touching an in-progress position of other rows.
    /// Takes a full entry because marking an item never played yet inserts the row. Watched marks
    /// clear the resume position; unwatched marks also reset the play count.
    /// </summary>
    Task SetCompletedAsync(WatchHistoryEntry entry, bool completed, CancellationToken cancellationToken);

    Task<IReadOnlyList<WatchHistoryEntry>> GetRecentAsync(long profileId, int limit, CancellationToken cancellationToken);

    Task<WatchHistoryEntry?> GetAsync(long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken);

    /// <summary>All episode entries of one series (item keys prefixed <c>{seriesProviderId}:</c>).</summary>
    Task<IReadOnlyList<WatchHistoryEntry>> GetForSeriesAsync(long profileId, string seriesProviderId, CancellationToken cancellationToken);

    /// <summary>Batch lookup for grids; safe for any key count (queries are chunked internally).</summary>
    Task<IReadOnlyList<WatchHistoryEntry>> GetByKeysAsync(
        long profileId, ContentKind kind, IReadOnlyCollection<string> itemKeys, CancellationToken cancellationToken);

    /// <summary>
    /// Per-series watch totals (keyed by series provider id) for grid progress bars:
    /// <c>WatchedUnits</c> counts each completed episode as 1 and each in-progress episode as
    /// its watched fraction. Chunked internally like <see cref="GetByKeysAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, SeriesWatchSummary>> GetSeriesWatchSummaryAsync(
        long profileId, IReadOnlyCollection<string> seriesProviderIds, CancellationToken cancellationToken);

    /// <summary>All completed VOD entries of a profile (movies and episodes), for Trakt push.</summary>
    Task<IReadOnlyList<WatchHistoryEntry>> GetCompletedAsync(long profileId, CancellationToken cancellationToken);

    Task DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <summary>Cache of provider item → Trakt identity matches.</summary>
public interface ITraktMatchRepository
{
    Task<TraktMatch?> GetAsync(long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<TraktMatch>> GetAllAsync(long profileId, CancellationToken cancellationToken);

    Task UpsertAsync(TraktMatch match, CancellationToken cancellationToken);

    /// <summary>Drops "found nothing" rows so a newly connected account gets a fresh look.</summary>
    Task ClearNegativeAsync(CancellationToken cancellationToken);
}

/// <summary>Snapshot of the connected Trakt account's watched history (app-global).</summary>
public interface ITraktWatchedRepository
{
    /// <summary>Atomically replaces the whole snapshot with a fresh pull.</summary>
    Task ReplaceAllAsync(IReadOnlyList<TraktWatchedItem> items, CancellationToken cancellationToken);

    Task<IReadOnlyList<TraktWatchedItem>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TraktWatchedItem>> GetMoviesAsync(CancellationToken cancellationToken);

    /// <summary>Watched episodes of one show, by the show's Trakt id.</summary>
    Task<IReadOnlyList<TraktWatchedItem>> GetEpisodesForShowAsync(long traktShowId, CancellationToken cancellationToken);

    /// <summary>Removes a movie (or a show's episode rows) after a history removal is pushed to Trakt.</summary>
    Task DeleteAsync(TraktMediaType mediaType, long traktId, int? season, int? episodeNumber, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Key/value settings; profileId 0 holds app-global values.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(long profileId, string key, CancellationToken cancellationToken);

    Task SetAsync(long profileId, string key, string value, CancellationToken cancellationToken);

    Task DeleteAsync(long profileId, string key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(long profileId, CancellationToken cancellationToken);
}

/// <summary>Cache of external artwork lookups (posters/backdrops by cleaned title).</summary>
public interface IArtworkCacheRepository
{
    Task<ArtworkCacheEntry?> GetAsync(ContentKind kind, string titleKey, int year, CancellationToken cancellationToken);

    Task UpsertAsync(ArtworkCacheEntry entry, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>Drops "found nothing" entries so a newly configured source gets a fresh look.</summary>
    Task ClearNegativeAsync(CancellationToken cancellationToken);
}

/// <summary>Disk cache for channel logos and posters.</summary>
public interface IImageCache
{
    /// <summary>
    /// Returns a local file path for the image, downloading it on first use.
    /// Null when the download fails (callers fall back to a monogram).
    /// </summary>
    Task<string?> GetLocalPathAsync(string url, CancellationToken cancellationToken);

    Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
