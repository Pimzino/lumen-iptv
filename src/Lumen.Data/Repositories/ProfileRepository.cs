using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IProfileRepository"/>.</summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ProfileRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<Profile>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Profile>(new CommandDefinition(
                "SELECT * FROM profiles ORDER BY id", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<Profile?> GetAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<Profile>(new CommandDefinition(
                "SELECT * FROM profiles WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<long> InsertAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO profiles
                        (name, kind, server_url, username, password_protected, playlist_source, playlist_is_file,
                         epg_source, epg_is_file, prefer_hls, stream_user_agent, avatar_color, created_utc, last_used_utc)
                    VALUES
                        (@Name, @Kind, @ServerUrl, @Username, @PasswordProtected, @PlaylistSource, @PlaylistIsFile,
                         @EpgSource, @EpgIsFile, @PreferHls, @StreamUserAgent, @AvatarColor, @CreatedUtc, @LastUsedUtc);
                    SELECT last_insert_rowid();
                    """,
                    profile, cancellationToken: cancellationToken)).ConfigureAwait(false);
                profile.Id = id;
                return id;
            }
        }, cancellationToken);
    }

    public Task UpdateAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE profiles SET
                        name = @Name, kind = @Kind, server_url = @ServerUrl, username = @Username,
                        password_protected = @PasswordProtected, playlist_source = @PlaylistSource,
                        playlist_is_file = @PlaylistIsFile, epg_source = @EpgSource, epg_is_file = @EpgIsFile,
                        prefer_hls = @PreferHls, stream_user_agent = @StreamUserAgent,
                        avatar_color = @AvatarColor, last_used_utc = @LastUsedUtc
                    WHERE id = @Id
                    """,
                    profile, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM profiles WHERE id = @id", new { id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task TouchLastUsedAsync(long id, long unixSeconds, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE profiles SET last_used_utc = @unixSeconds WHERE id = @id",
                new { id, unixSeconds }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);
}
