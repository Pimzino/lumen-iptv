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
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO watch_history
                        (profile_id, item_kind, item_key, title, poster_url, position_seconds, duration_seconds, watched_utc)
                    VALUES (@ProfileId, @ItemKind, @ItemKey, @Title, @PosterUrl, @PositionSeconds, @DurationSeconds, @WatchedUtc)
                    ON CONFLICT (profile_id, item_kind, item_key) DO UPDATE SET
                        title = excluded.title,
                        poster_url = excluded.poster_url,
                        position_seconds = excluded.position_seconds,
                        duration_seconds = excluded.duration_seconds,
                        watched_utc = excluded.watched_utc
                    """,
                    entry, cancellationToken: cancellationToken)).ConfigureAwait(false);
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
