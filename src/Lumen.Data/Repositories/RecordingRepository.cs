using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IRecordingRepository"/>.</summary>
public sealed class RecordingRepository : IRecordingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RecordingRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<Recording>> GetAllAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<Recording>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Recording>(new CommandDefinition(
                "SELECT * FROM recordings WHERE profile_id = @profileId ORDER BY started_utc DESC",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<long> InsertAsync(Recording recording, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO recordings
                        (profile_id, channel_id, channel_name, programme_title, logo_url, file_path,
                         status, error, started_utc, stopped_utc, duration_seconds, size_bytes)
                    VALUES
                        (@ProfileId, @ChannelId, @ChannelName, @ProgrammeTitle, @LogoUrl, @FilePath,
                         @Status, @Error, @StartedUtc, @StoppedUtc, @DurationSeconds, @SizeBytes);
                    SELECT last_insert_rowid();
                    """,
                    recording, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task UpdateStatusAsync(
        long id,
        DownloadStatus status,
        string? error,
        long? stoppedUtc,
        long? durationSeconds,
        long sizeBytes,
        CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE recordings SET
                    status = @status, error = @error, stopped_utc = @stoppedUtc,
                    duration_seconds = @durationSeconds, size_bytes = @sizeBytes
                WHERE id = @id
                """,
                new { id, status, error, stoppedUtc, durationSeconds, sizeBytes },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<Recording>> GetByStatusAsync(
        DownloadStatus status, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<Recording>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Recording>(new CommandDefinition(
                "SELECT * FROM recordings WHERE status = @status ORDER BY started_utc",
                new { status }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task UpdateTitleAsync(long id, string? customTitle, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE recordings SET custom_title = @customTitle WHERE id = @id",
                new { id, customTitle }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task DeleteAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM recordings WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<Recording>> GetByProfileForCleanupAsync(
        long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<Recording>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Recording>(new CommandDefinition(
                "SELECT * FROM recordings WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);
}
