using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="ICatalogRepository"/> with snapshot-style provider syncs.</summary>
public sealed class CatalogRepository : ICatalogRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CatalogRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(
        long profileId, ContentKind kind, CancellationToken cancellationToken) => DbOffload.Run<IReadOnlyList<Category>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Category>(new CommandDefinition(
                "SELECT * FROM categories WHERE profile_id = @profileId AND kind = @kind ORDER BY sort_order, name",
                new { profileId, kind }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task ReplaceCategoriesAsync(
        long profileId, ContentKind kind, IReadOnlyList<Category> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var transaction = connection.BeginTransaction();

                var existing = (await connection.QueryAsync<(long Id, string ProviderCategoryId)>(
                    "SELECT id, provider_category_id FROM categories WHERE profile_id = @profileId AND kind = @kind",
                    new { profileId, kind }, transaction).ConfigureAwait(false))
                    .ToDictionary(r => r.ProviderCategoryId, r => r.Id, StringComparer.Ordinal);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var category in categories)
                {
                    seen.Add(category.ProviderCategoryId);
                    if (existing.TryGetValue(category.ProviderCategoryId, out var id))
                    {
                        category.Id = id;
                        await connection.ExecuteAsync(
                            "UPDATE categories SET name = @Name, sort_order = @SortOrder WHERE id = @Id",
                            category, transaction).ConfigureAwait(false);
                    }
                    else
                    {
                        category.Id = await connection.ExecuteScalarAsync<long>(
                            """
                            INSERT INTO categories (profile_id, provider_category_id, kind, name, sort_order, content_kind_override)
                            VALUES (@ProfileId, @ProviderCategoryId, @Kind, @Name, @SortOrder, @ContentKindOverride);
                            SELECT last_insert_rowid();
                            """,
                            new
                            {
                                ProfileId = profileId,
                                category.ProviderCategoryId,
                                Kind = kind,
                                category.Name,
                                category.SortOrder,
                                category.ContentKindOverride,
                            },
                            transaction).ConfigureAwait(false);
                    }
                }

                var removed = existing.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                if (removed.Count > 0)
                {
                    foreach (var chunk in Chunk(removed))
                    {
                        await connection.ExecuteAsync(
                            "DELETE FROM categories WHERE id IN @chunk", new { chunk }, transaction).ConfigureAwait(false);
                    }
                }

                transaction.Commit();
            }
        }, cancellationToken);
    }

    public Task UpsertChannelsAsync(
        long profileId, IReadOnlyList<Channel> channels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var transaction = connection.BeginTransaction();

                await connection.ExecuteAsync(
                    """
                    CREATE TEMP TABLE staging_channels (
                      provider_stream_id TEXT NULL,
                      category_id INTEGER NULL,
                      number INTEGER NULL,
                      name TEXT NOT NULL,
                      logo_url TEXT NULL,
                      stream_url TEXT NULL,
                      epg_channel_id TEXT NULL,
                      tvg_shift_minutes INTEGER NOT NULL,
                      user_agent TEXT NULL,
                      referrer TEXT NULL,
                      has_archive INTEGER NOT NULL,
                      archive_days INTEGER NOT NULL,
                      added_utc INTEGER NOT NULL,
                      match_key TEXT NOT NULL
                    );
                    """, transaction: transaction).ConfigureAwait(false);

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO staging_channels VALUES ($psid, $cat, $num, $name, $logo, $url, $epg, $shift, $ua, $ref, $archive, $archiveDays, $added, $key);";
                    var pPsid = insert.Parameters.Add("$psid", SqliteType.Text);
                    var pCat = insert.Parameters.Add("$cat", SqliteType.Integer);
                    var pNum = insert.Parameters.Add("$num", SqliteType.Integer);
                    var pName = insert.Parameters.Add("$name", SqliteType.Text);
                    var pLogo = insert.Parameters.Add("$logo", SqliteType.Text);
                    var pUrl = insert.Parameters.Add("$url", SqliteType.Text);
                    var pEpg = insert.Parameters.Add("$epg", SqliteType.Text);
                    var pShift = insert.Parameters.Add("$shift", SqliteType.Integer);
                    var pUa = insert.Parameters.Add("$ua", SqliteType.Text);
                    var pRef = insert.Parameters.Add("$ref", SqliteType.Text);
                    var pArchive = insert.Parameters.Add("$archive", SqliteType.Integer);
                    var pArchiveDays = insert.Parameters.Add("$archiveDays", SqliteType.Integer);
                    var pAdded = insert.Parameters.Add("$added", SqliteType.Integer);
                    var pKey = insert.Parameters.Add("$key", SqliteType.Text);

                    foreach (var channel in channels)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pPsid.Value = (object?)channel.ProviderStreamId ?? DBNull.Value;
                        pCat.Value = (object?)channel.CategoryId ?? DBNull.Value;
                        pNum.Value = (object?)channel.Number ?? DBNull.Value;
                        pName.Value = channel.Name;
                        pLogo.Value = (object?)channel.LogoUrl ?? DBNull.Value;
                        pUrl.Value = (object?)channel.StreamUrl ?? DBNull.Value;
                        pEpg.Value = (object?)channel.EpgChannelId ?? DBNull.Value;
                        pShift.Value = channel.TvgShiftMinutes;
                        pUa.Value = (object?)channel.UserAgent ?? DBNull.Value;
                        pRef.Value = (object?)channel.Referrer ?? DBNull.Value;
                        pArchive.Value = channel.HasArchive ? 1 : 0;
                        pArchiveDays.Value = channel.ArchiveDays;
                        pAdded.Value = channel.AddedUtc;
                        pKey.Value = channel.ProviderStreamId ?? channel.StreamUrl ?? channel.Name;
                        insert.ExecuteNonQuery();
                    }
                }

                // Update matched rows, insert new rows, delete rows gone from the snapshot.
                await connection.ExecuteAsync(
                    """
                    UPDATE channels SET
                        category_id = s.category_id,
                        number = s.number,
                        name = s.name,
                        logo_url = s.logo_url,
                        stream_url = s.stream_url,
                        epg_channel_id = s.epg_channel_id,
                        tvg_shift_minutes = s.tvg_shift_minutes,
                        user_agent = s.user_agent,
                        referrer = s.referrer,
                        has_archive = s.has_archive,
                        archive_days = s.archive_days
                    FROM staging_channels AS s
                    WHERE channels.profile_id = @profileId
                      AND COALESCE(channels.provider_stream_id, channels.stream_url, channels.name) = s.match_key;
                    """, new { profileId }, transaction).ConfigureAwait(false);

                await connection.ExecuteAsync(
                    """
                    INSERT INTO channels
                        (profile_id, category_id, provider_stream_id, number, name, logo_url, stream_url,
                         epg_channel_id, tvg_shift_minutes, user_agent, referrer, has_archive, archive_days,
                         is_hidden, added_utc)
                    SELECT @profileId, s.category_id, s.provider_stream_id, s.number, s.name, s.logo_url, s.stream_url,
                           s.epg_channel_id, s.tvg_shift_minutes, s.user_agent, s.referrer, s.has_archive, s.archive_days,
                           0, s.added_utc
                    FROM staging_channels AS s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM channels c
                        WHERE c.profile_id = @profileId
                          AND COALESCE(c.provider_stream_id, c.stream_url, c.name) = s.match_key);
                    """, new { profileId }, transaction).ConfigureAwait(false);

                await connection.ExecuteAsync(
                    """
                    DELETE FROM channels
                    WHERE profile_id = @profileId
                      AND NOT EXISTS (
                        SELECT 1 FROM staging_channels s
                        WHERE s.match_key = COALESCE(channels.provider_stream_id, channels.stream_url, channels.name));
                    """, new { profileId }, transaction).ConfigureAwait(false);

                await connection.ExecuteAsync("DROP TABLE staging_channels;", transaction: transaction).ConfigureAwait(false);
                transaction.Commit();
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<Channel>> GetChannelsAsync(
        long profileId, long? categoryId, CancellationToken cancellationToken) => DbOffload.Run<IReadOnlyList<Channel>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var sql = categoryId is null
                ? "SELECT * FROM channels WHERE profile_id = @profileId AND is_hidden = 0 ORDER BY number, name"
                : "SELECT * FROM channels WHERE profile_id = @profileId AND category_id = @categoryId AND is_hidden = 0 ORDER BY number, name";
            var rows = await connection.QueryAsync<Channel>(new CommandDefinition(
                sql, new { profileId, categoryId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<Channel?> GetChannelAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<Channel>(new CommandDefinition(
                "SELECT * FROM channels WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<int> CountChannelsAsync(long profileId, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM channels WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task UpsertVodItemsAsync(
        long profileId, ContentKind kind, IReadOnlyList<VodItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var transaction = connection.BeginTransaction();

                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText =
                        """
                        INSERT INTO vod_items
                            (profile_id, kind, provider_item_id, category_id, name, poster_url, rating, year,
                             provider_added_utc, container_extension, stream_url)
                        VALUES ($profile, $kind, $pid, $cat, $name, $poster, $rating, $year, $added, $ext, $url)
                        ON CONFLICT (profile_id, kind, provider_item_id) DO UPDATE SET
                            category_id = excluded.category_id,
                            name = excluded.name,
                            poster_url = excluded.poster_url,
                            rating = excluded.rating,
                            year = excluded.year,
                            provider_added_utc = excluded.provider_added_utc,
                            container_extension = excluded.container_extension,
                            stream_url = excluded.stream_url;
                        """;
                    upsert.Parameters.AddWithValue("$profile", profileId);
                    upsert.Parameters.AddWithValue("$kind", (int)kind);
                    var pPid = upsert.Parameters.Add("$pid", SqliteType.Text);
                    var pCat = upsert.Parameters.Add("$cat", SqliteType.Integer);
                    var pName = upsert.Parameters.Add("$name", SqliteType.Text);
                    var pPoster = upsert.Parameters.Add("$poster", SqliteType.Text);
                    var pRating = upsert.Parameters.Add("$rating", SqliteType.Real);
                    var pYear = upsert.Parameters.Add("$year", SqliteType.Integer);
                    var pAdded = upsert.Parameters.Add("$added", SqliteType.Integer);
                    var pExt = upsert.Parameters.Add("$ext", SqliteType.Text);
                    var pUrl = upsert.Parameters.Add("$url", SqliteType.Text);

                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pPid.Value = item.ProviderItemId;
                        pCat.Value = (object?)item.CategoryId ?? DBNull.Value;
                        pName.Value = item.Name;
                        pPoster.Value = (object?)item.PosterUrl ?? DBNull.Value;
                        pRating.Value = (object?)item.Rating ?? DBNull.Value;
                        pYear.Value = (object?)item.Year ?? DBNull.Value;
                        pAdded.Value = (object?)item.ProviderAddedUtc ?? DBNull.Value;
                        pExt.Value = (object?)item.ContainerExtension ?? DBNull.Value;
                        pUrl.Value = (object?)item.StreamUrl ?? DBNull.Value;
                        upsert.ExecuteNonQuery();
                    }
                }

                // Remove items missing from the snapshot.
                var providerIds = items.Select(i => i.ProviderItemId).ToList();
                if (providerIds.Count == 0)
                {
                    await connection.ExecuteAsync(
                        "DELETE FROM vod_items WHERE profile_id = @profileId AND kind = @kind",
                        new { profileId, kind }, transaction).ConfigureAwait(false);
                }
                else
                {
                    await connection.ExecuteAsync(
                        "CREATE TEMP TABLE staging_vod_ids (provider_item_id TEXT PRIMARY KEY);",
                        transaction: transaction).ConfigureAwait(false);
                    foreach (var chunk in Chunk(providerIds))
                    {
                        await connection.ExecuteAsync(
                            "INSERT OR IGNORE INTO staging_vod_ids (provider_item_id) VALUES (@id)",
                            chunk.Select(id => new { id }), transaction).ConfigureAwait(false);
                    }

                    await connection.ExecuteAsync(
                        """
                        DELETE FROM vod_items
                        WHERE profile_id = @profileId AND kind = @kind
                          AND provider_item_id NOT IN (SELECT provider_item_id FROM staging_vod_ids);
                        """, new { profileId, kind }, transaction).ConfigureAwait(false);
                    await connection.ExecuteAsync("DROP TABLE staging_vod_ids;", transaction: transaction).ConfigureAwait(false);
                }

                transaction.Commit();
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<VodItem>> GetVodItemsAsync(
        long profileId,
        ContentKind kind,
        long? categoryId,
        string? search,
        VodSortOrder sort,
        int limit,
        int offset,
        CancellationToken cancellationToken) => DbOffload.Run<IReadOnlyList<VodItem>>(async () =>
    {
        var orderBy = sort switch
        {
            VodSortOrder.Name => "name COLLATE NOCASE ASC",
            VodSortOrder.Rating => "rating DESC NULLS LAST, name COLLATE NOCASE ASC",
            _ => "provider_added_utc DESC NULLS LAST, name COLLATE NOCASE ASC",
        };

        var filter = categoryId is null ? string.Empty : "AND category_id = @categoryId";
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var searchFilter = term is null ? string.Empty : "AND name LIKE @pattern ESCAPE '\\'";
        var pattern = term is null ? null : $"%{SqlLike.Escape(term)}%";
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<VodItem>(new CommandDefinition(
                $"""
                 SELECT * FROM vod_items
                 WHERE profile_id = @profileId AND kind = @kind {filter} {searchFilter}
                 ORDER BY {orderBy}
                 LIMIT @limit OFFSET @offset
                 """,
                new { profileId, kind, categoryId, pattern, limit, offset },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<VodItem?> GetVodItemAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<VodItem>(new CommandDefinition(
                "SELECT * FROM vod_items WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<VodItem?> GetVodItemByProviderIdAsync(
        long profileId, ContentKind kind, string providerItemId, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<VodItem>(new CommandDefinition(
                "SELECT * FROM vod_items WHERE profile_id = @profileId AND kind = @kind AND provider_item_id = @providerItemId",
                new { profileId, kind, providerItemId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<VodItem>> GetRecentVodAsync(
        long profileId, ContentKind kind, int limit, CancellationToken cancellationToken) =>
        GetVodItemsAsync(profileId, kind, categoryId: null, search: null, VodSortOrder.Added, limit, 0, cancellationToken);

    public Task SetCategoryKindOverrideAsync(
        long categoryId, ContentKind? kind, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE categories SET content_kind_override = @kind WHERE id = @categoryId",
                new { categoryId, kind }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size = 500)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
