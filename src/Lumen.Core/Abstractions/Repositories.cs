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

    Task<IReadOnlyList<VodItem>> GetVodItemsAsync(
        long profileId,
        ContentKind kind,
        long? categoryId,
        VodSortOrder sort,
        int limit,
        int offset,
        CancellationToken cancellationToken);

    Task<VodItem?> GetVodItemAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<VodItem>> GetRecentVodAsync(long profileId, ContentKind kind, int limit, CancellationToken cancellationToken);

    Task SetCategoryKindOverrideAsync(long categoryId, ContentKind? kind, CancellationToken cancellationToken);
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

/// <summary>Watch history with VOD resume positions.</summary>
public interface IWatchHistoryRepository
{
    Task UpsertAsync(WatchHistoryEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<WatchHistoryEntry>> GetRecentAsync(long profileId, int limit, CancellationToken cancellationToken);

    Task<WatchHistoryEntry?> GetAsync(long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken);

    Task DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <summary>Key/value settings; profileId 0 holds app-global values.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(long profileId, string key, CancellationToken cancellationToken);

    Task SetAsync(long profileId, string key, string value, CancellationToken cancellationToken);

    Task DeleteAsync(long profileId, string key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(long profileId, CancellationToken cancellationToken);
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
