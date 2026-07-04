using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="ITraktMatchRepository"/>.</summary>
public sealed class TraktMatchRepository : ITraktMatchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TraktMatchRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<TraktMatch?> GetAsync(
        long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QuerySingleOrDefaultAsync<TraktMatch>(new CommandDefinition(
                "SELECT * FROM trakt_match WHERE profile_id = @profileId AND item_kind = @kind AND item_key = @itemKey",
                new { profileId, kind, itemKey }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<TraktMatch>> GetAllAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<TraktMatch>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<TraktMatch>(new CommandDefinition(
                "SELECT * FROM trakt_match WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task UpsertAsync(TraktMatch match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO trakt_match
                        (profile_id, item_kind, item_key, trakt_id, tmdb_id, imdb_id,
                         matched_title, matched_year, method, matched_utc)
                    VALUES (@ProfileId, @ItemKind, @ItemKey, @TraktId, @TmdbId, @ImdbId,
                         @MatchedTitle, @MatchedYear, @Method, @MatchedUtc)
                    ON CONFLICT (profile_id, item_kind, item_key) DO UPDATE SET
                        trakt_id = excluded.trakt_id,
                        tmdb_id = excluded.tmdb_id,
                        imdb_id = excluded.imdb_id,
                        matched_title = excluded.matched_title,
                        matched_year = excluded.matched_year,
                        method = excluded.method,
                        matched_utc = excluded.matched_utc
                    """,
                    match, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task ClearNegativeAsync(CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trakt_match WHERE trakt_id IS NULL AND tmdb_id IS NULL AND imdb_id IS NULL",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);
}

/// <summary>SQLite-backed <see cref="ITraktWatchedRepository"/>.</summary>
public sealed class TraktWatchedRepository : ITraktWatchedRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TraktWatchedRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task ReplaceAllAsync(IReadOnlyList<TraktWatchedItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        return DbOffload.Run(async () =>
        {
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var transaction = connection.BeginTransaction();
                await connection.ExecuteAsync(
                    "DELETE FROM trakt_watched", transaction: transaction).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO trakt_watched
                        (media_type, trakt_id, tmdb_id, imdb_id, title, year,
                         season, episode_number, plays, last_watched_utc)
                    VALUES (@MediaType, @TraktId, @TmdbId, @ImdbId, @Title, @Year,
                         @Season, @EpisodeNumber, @Plays, @LastWatchedUtc)
                    ON CONFLICT (media_type, trakt_id, season, episode_number) DO UPDATE SET
                        plays = MAX(plays, excluded.plays),
                        last_watched_utc = MAX(last_watched_utc, excluded.last_watched_utc)
                    """,
                    items, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                transaction.Commit();
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TraktWatchedItem>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM trakt_watched", null, cancellationToken);

    public Task<IReadOnlyList<TraktWatchedItem>> GetMoviesAsync(CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM trakt_watched WHERE media_type = @mediaType",
            new { mediaType = TraktMediaType.Movie }, cancellationToken);

    public Task<IReadOnlyList<TraktWatchedItem>> GetEpisodesForShowAsync(
        long traktShowId, CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM trakt_watched WHERE media_type = @mediaType AND trakt_id = @traktShowId",
            new { mediaType = TraktMediaType.Episode, traktShowId }, cancellationToken);

    public Task DeleteAsync(
        TraktMediaType mediaType, long traktId, int? season, int? episodeNumber, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM trakt_watched
                WHERE media_type = @mediaType AND trakt_id = @traktId
                  AND (@season IS NULL OR season = @season)
                  AND (@episodeNumber IS NULL OR episode_number = @episodeNumber)
                """,
                new { mediaType, traktId, season, episodeNumber },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken) => DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trakt_watched", cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    private Task<IReadOnlyList<TraktWatchedItem>> QueryAsync(
        string sql, object? parameters, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<TraktWatchedItem>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<TraktWatchedItem>(new CommandDefinition(
                sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);
}
