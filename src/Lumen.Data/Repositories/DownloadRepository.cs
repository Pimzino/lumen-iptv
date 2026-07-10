using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IDownloadRepository"/>.</summary>
public sealed class DownloadRepository : IDownloadRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DownloadRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<DownloadItem>> GetAllAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<DownloadItem>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<DownloadItem>(new CommandDefinition(
                "SELECT * FROM downloads WHERE profile_id = @profileId ORDER BY created_utc DESC",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<DownloadItem?> GetByItemKeyAsync(
        long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await connection.QuerySingleOrDefaultAsync<DownloadItem>(new CommandDefinition(
                    "SELECT * FROM downloads WHERE profile_id = @profileId AND kind = @kind AND item_key = @itemKey",
                    new { profileId, kind, itemKey }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DownloadItem>> GetByStatusesAsync(
        IReadOnlyCollection<DownloadStatus> statuses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return DbOffload.Run<IReadOnlyList<DownloadItem>>(async () =>
        {
            if (statuses.Count == 0)
            {
                return [];
            }

            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                // Enum collection → underlying ints so Dapper's IN expansion binds cleanly.
                var codes = statuses.Select(s => (int)s).ToArray();
                var rows = await connection.QueryAsync<DownloadItem>(new CommandDefinition(
                    "SELECT * FROM downloads WHERE status IN @codes ORDER BY created_utc",
                    new { codes }, cancellationToken: cancellationToken)).ConfigureAwait(false);
                return rows.AsList();
            }
        }, cancellationToken);
    }

    public Task<long> InsertAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                // Idempotent: a second enqueue of the same item is a no-op and returns the existing id.
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO downloads
                        (profile_id, kind, item_key, series_item_key, provider_item_id, container_extension,
                         stream_url, title, poster_url, season, episode_number, is_hls, file_path, status,
                         total_bytes, downloaded_bytes, progress_permille, error, created_utc, completed_utc)
                    VALUES
                        (@ProfileId, @Kind, @ItemKey, @SeriesItemKey, @ProviderItemId, @ContainerExtension,
                         @StreamUrl, @Title, @PosterUrl, @Season, @EpisodeNumber, @IsHls, @FilePath, @Status,
                         @TotalBytes, @DownloadedBytes, @ProgressPermille, @Error, @CreatedUtc, @CompletedUtc)
                    ON CONFLICT (profile_id, kind, item_key) DO NOTHING
                    """,
                    item, cancellationToken: cancellationToken)).ConfigureAwait(false);

                return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM downloads WHERE profile_id = @ProfileId AND kind = @Kind AND item_key = @ItemKey",
                    item, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task UpdateStatusAsync(
        long id, DownloadStatus status, string? error, long? completedUtc, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE downloads SET status = @status, error = @error, completed_utc = @completedUtc
                WHERE id = @id
                """,
                new { id, status, error, completedUtc }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task UpdateProgressAsync(
        long id, long downloadedBytes, long? totalBytes, int progressPermille, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE downloads
                SET downloaded_bytes = @downloadedBytes, total_bytes = @totalBytes, progress_permille = @progressPermille
                WHERE id = @id
                """,
                new { id, downloadedBytes, totalBytes, progressPermille },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task UpdateIsHlsAsync(long id, bool isHls, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE downloads SET is_hls = @isHls WHERE id = @id",
                new { id, isHls }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task DeleteAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM downloads WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<DownloadItem>> GetByProfileForCleanupAsync(
        long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<DownloadItem>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<DownloadItem>(new CommandDefinition(
                "SELECT * FROM downloads WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);
}
