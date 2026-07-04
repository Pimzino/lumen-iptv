using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IFavoritesRepository"/>.</summary>
public sealed class FavoritesRepository : IFavoritesRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public FavoritesRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<FavoriteItem>> GetAllAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<FavoriteItem>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<FavoriteItem>(new CommandDefinition(
                "SELECT * FROM favorites WHERE profile_id = @profileId ORDER BY added_utc DESC",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task AddAsync(
        long profileId, ContentKind kind, string itemKey, long unixSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO favorites (profile_id, item_kind, item_key, added_utc)
                    VALUES (@profileId, @kind, @itemKey, @unixSeconds)
                    ON CONFLICT (profile_id, item_kind, item_key) DO NOTHING
                    """,
                    new { profileId, kind, itemKey, unixSeconds },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task RemoveAsync(long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM favorites WHERE profile_id = @profileId AND item_kind = @kind AND item_key = @itemKey",
                new { profileId, kind, itemKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);
}

/// <summary>SQLite-backed <see cref="IWatchHistoryRepository"/>.</summary>
public sealed class WatchHistoryRepository : IWatchHistoryRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public WatchHistoryRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task UpsertAsync(WatchHistoryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                // Completion state merges rather than replaces: a later partial-progress save (or a
                // zero-filled live-TV entry) must never clear the watched flag or the play count.
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO watch_history
                        (profile_id, item_kind, item_key, title, poster_url, position_seconds, duration_seconds,
                         watched_utc, completed, play_count, completed_utc, season, episode_number)
                    VALUES (@ProfileId, @ItemKind, @ItemKey, @Title, @PosterUrl, @PositionSeconds, @DurationSeconds,
                         @WatchedUtc, @Completed, @PlayCount, @CompletedUtc, @Season, @EpisodeNumber)
                    ON CONFLICT (profile_id, item_kind, item_key) DO UPDATE SET
                        title = excluded.title,
                        poster_url = excluded.poster_url,
                        position_seconds = excluded.position_seconds,
                        duration_seconds = excluded.duration_seconds,
                        watched_utc = excluded.watched_utc,
                        completed = MAX(completed, excluded.completed),
                        play_count = play_count + excluded.play_count,
                        completed_utc = COALESCE(excluded.completed_utc, completed_utc),
                        season = COALESCE(excluded.season, season),
                        episode_number = COALESCE(excluded.episode_number, episode_number)
                    """,
                    entry, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task SetCompletedAsync(WatchHistoryEntry entry, bool completed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                if (completed)
                {
                    // Watched: keep the higher play count (Trakt and local both count plays; taking
                    // MAX avoids double-counting scrobbled sessions) and clear the resume position.
                    await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO watch_history
                            (profile_id, item_kind, item_key, title, poster_url, position_seconds, duration_seconds,
                             watched_utc, completed, play_count, completed_utc, season, episode_number)
                        VALUES (@ProfileId, @ItemKind, @ItemKey, @Title, @PosterUrl, 0, @DurationSeconds,
                             @WatchedUtc, 1, MAX(@PlayCount, 1), @CompletedUtc, @Season, @EpisodeNumber)
                        ON CONFLICT (profile_id, item_kind, item_key) DO UPDATE SET
                            completed = 1,
                            play_count = MAX(play_count, excluded.play_count),
                            completed_utc = COALESCE(excluded.completed_utc, completed_utc),
                            watched_utc = MAX(watched_utc, excluded.watched_utc),
                            position_seconds = 0,
                            season = COALESCE(excluded.season, season),
                            episode_number = COALESCE(excluded.episode_number, episode_number)
                        """,
                        entry, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
                else
                {
                    // Unwatched: reset completion and plays but keep the row (title/poster stay
                    // useful for the recently-watched rail).
                    await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE watch_history SET
                            completed = 0, play_count = 0, completed_utc = NULL, position_seconds = 0
                        WHERE profile_id = @ProfileId AND item_kind = @ItemKind AND item_key = @ItemKey
                        """,
                        entry, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WatchHistoryEntry>> GetRecentAsync(
        long profileId, int limit, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<WatchHistoryEntry>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<WatchHistoryEntry>(new CommandDefinition(
                "SELECT * FROM watch_history WHERE profile_id = @profileId ORDER BY watched_utc DESC LIMIT @limit",
                new { profileId, limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<WatchHistoryEntry?> GetAsync(
        long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<WatchHistoryEntry>(new CommandDefinition(
                "SELECT * FROM watch_history WHERE profile_id = @profileId AND item_kind = @kind AND item_key = @itemKey",
                new { profileId, kind, itemKey }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<WatchHistoryEntry>> GetForSeriesAsync(
        long profileId, string seriesProviderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesProviderId);
        return DbOffload.Run<IReadOnlyList<WatchHistoryEntry>>(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                // Episode keys are "{seriesProviderId}:{episodeId}"; ';' is the code point after
                // ':', so this range is exactly the key prefix and stays on the unique index
                // (LIKE would fall back to a scan under NOCASE).
                var rows = await connection.QueryAsync<WatchHistoryEntry>(new CommandDefinition(
                    """
                    SELECT * FROM watch_history
                    WHERE profile_id = @profileId AND item_kind = @kind AND item_key >= @low AND item_key < @high
                    """,
                    new { profileId, kind = ContentKind.Series, low = seriesProviderId + ":", high = seriesProviderId + ";" },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                return rows.AsList();
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WatchHistoryEntry>> GetByKeysAsync(
        long profileId, ContentKind kind, IReadOnlyCollection<string> itemKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemKeys);
        return DbOffload.Run<IReadOnlyList<WatchHistoryEntry>>(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var results = new List<WatchHistoryEntry>(itemKeys.Count);
                foreach (var chunk in itemKeys.Chunk(400)) // SQLite parameter limit headroom
                {
                    var rows = await connection.QueryAsync<WatchHistoryEntry>(new CommandDefinition(
                        "SELECT * FROM watch_history WHERE profile_id = @profileId AND item_kind = @kind AND item_key IN @chunk",
                        new { profileId, kind, chunk }, cancellationToken: cancellationToken)).ConfigureAwait(false);
                    results.AddRange(rows);
                }

                return results;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, SeriesWatchSummary>> GetSeriesWatchSummaryAsync(
        long profileId, IReadOnlyCollection<string> seriesProviderIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesProviderIds);
        return DbOffload.Run<IReadOnlyDictionary<string, SeriesWatchSummary>>(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var result = new Dictionary<string, SeriesWatchSummary>(StringComparer.Ordinal);
                foreach (var chunk in seriesProviderIds.Chunk(400))
                {
                    // Episode keys are "{seriesProviderId}:{episodeId}" — aggregate by the prefix.
                    // A completed episode is one unit; an in-progress one counts its fraction.
                    var rows = await connection.QueryAsync<(string SeriesKey, double WatchedUnits, int CompletedCount)>(
                        new CommandDefinition(
                            """
                            SELECT substr(item_key, 1, instr(item_key, ':') - 1) AS SeriesKey,
                                   SUM(CASE
                                       WHEN completed = 1 THEN 1.0
                                       WHEN duration_seconds > 0 THEN MIN(position_seconds / duration_seconds, 1.0)
                                       ELSE 0.0 END) AS WatchedUnits,
                                   SUM(completed) AS CompletedCount
                            FROM watch_history
                            WHERE profile_id = @profileId AND item_kind = @kind
                              AND instr(item_key, ':') > 0
                              AND substr(item_key, 1, instr(item_key, ':') - 1) IN @chunk
                            GROUP BY SeriesKey
                            """,
                            new { profileId, kind = ContentKind.Series, chunk },
                            cancellationToken: cancellationToken)).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        result[row.SeriesKey] = new SeriesWatchSummary(row.WatchedUnits, row.CompletedCount);
                    }
                }

                return result;
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WatchHistoryEntry>> GetCompletedAsync(
        long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<WatchHistoryEntry>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<WatchHistoryEntry>(new CommandDefinition(
                "SELECT * FROM watch_history WHERE profile_id = @profileId AND completed = 1 AND item_kind <> @live",
                new { profileId, live = ContentKind.Live }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task DeleteAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM watch_history WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);
}

/// <summary>SQLite-backed <see cref="ISettingsRepository"/>.</summary>
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<string?> GetAsync(long profileId, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT value FROM settings WHERE profile_id = @profileId AND key = @key",
                    new { profileId, key }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task SetAsync(long profileId, string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO settings (profile_id, key, value) VALUES (@profileId, @key, @value)
                    ON CONFLICT (profile_id, key) DO UPDATE SET value = excluded.value
                    """,
                    new { profileId, key, value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task DeleteAsync(long profileId, string key, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM settings WHERE profile_id = @profileId AND key = @key",
                new { profileId, key }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyDictionary<string, string>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<(string Key, string Value)>(new CommandDefinition(
                "SELECT key, value FROM settings WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        }
    }, cancellationToken);
}
