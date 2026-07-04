using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IArtworkCacheRepository"/>.</summary>
public sealed class ArtworkCacheRepository : IArtworkCacheRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ArtworkCacheRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<ArtworkCacheEntry?> GetAsync(
        ContentKind kind, string titleKey, int year, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<ArtworkCacheEntry>(new CommandDefinition(
                "SELECT * FROM artwork_cache WHERE kind = @kind AND title_key = @titleKey AND year = @year",
                new { kind, titleKey, year }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task UpsertAsync(ArtworkCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO artwork_cache (kind, title_key, year, poster_url, backdrop_url, provider, resolved_utc)
                    VALUES (@Kind, @TitleKey, @Year, @PosterUrl, @BackdropUrl, @Provider, @ResolvedUtc)
                    ON CONFLICT (kind, title_key, year) DO UPDATE SET
                        poster_url = excluded.poster_url,
                        backdrop_url = excluded.backdrop_url,
                        provider = excluded.provider,
                        resolved_utc = excluded.resolved_utc
                    """,
                    entry, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM artwork_cache", cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task ClearNegativeAsync(CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM artwork_cache WHERE poster_url IS NULL AND backdrop_url IS NULL",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);
}
