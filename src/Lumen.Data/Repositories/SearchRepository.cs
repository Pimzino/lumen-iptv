using Dapper;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>
/// Mutable row DTO for Dapper. Property-name mapping (rather than positional-record constructor
/// matching) is used because the latter is brittle with enums and untyped NULL columns.
/// </summary>
internal sealed class SearchHitRow
{
    public int Kind { get; set; }

    public string ItemKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public string? ImageUrl { get; set; }

    public SearchHit ToHit() => new((ContentKind)Kind, ItemKey, Title, Subtitle, ImageUrl);
}

/// <summary>SQLite-backed <see cref="ISearchRepository"/> using indexed LIKE prefix/substring matches.</summary>
public sealed class SearchRepository : ISearchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SearchRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<SearchResults> SearchAsync(
        long profileId, string query, long nowUnix, int perGroupLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(SearchResults.Empty);
        }

        return DbOffload.Run(async () =>
        {
            // Escape LIKE wildcards in user input, then match as a substring.
            var escaped = query.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            var pattern = $"%{escaped}%";

            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var channels = (await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
                    """
                    SELECT 0 AS Kind, CAST(id AS TEXT) AS ItemKey, name AS Title,
                           CAST(NULL AS TEXT) AS Subtitle, logo_url AS ImageUrl
                    FROM channels
                    WHERE profile_id = @profileId AND is_hidden = 0 AND name LIKE @pattern ESCAPE '\'
                    ORDER BY name COLLATE NOCASE
                    LIMIT @limit
                    """,
                    new { profileId, pattern, limit = perGroupLimit }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false)).AsList();

                var movies = (await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
                    """
                    SELECT 1 AS Kind, provider_item_id AS ItemKey, name AS Title,
                           CAST(NULL AS TEXT) AS Subtitle, poster_url AS ImageUrl
                    FROM vod_items
                    WHERE profile_id = @profileId AND kind = 1 AND name LIKE @pattern ESCAPE '\'
                    ORDER BY name COLLATE NOCASE
                    LIMIT @limit
                    """,
                    new { profileId, pattern, limit = perGroupLimit }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false)).AsList();

                var series = (await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
                    """
                    SELECT 2 AS Kind, provider_item_id AS ItemKey, name AS Title,
                           CAST(NULL AS TEXT) AS Subtitle, poster_url AS ImageUrl
                    FROM vod_items
                    WHERE profile_id = @profileId AND kind = 2 AND name LIKE @pattern ESCAPE '\'
                    ORDER BY name COLLATE NOCASE
                    LIMIT @limit
                    """,
                    new { profileId, pattern, limit = perGroupLimit }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false)).AsList();

                // Upcoming programmes (bounded to a 48h window so the title scan stays small),
                // mapped back to a playable channel via channel_epg_map.
                var horizon = nowUnix + 48 * 3600;
                var programmes = (await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
                    """
                    SELECT 0 AS Kind,
                           CAST(c.id AS TEXT) AS ItemKey,
                           p.title AS Title,
                           c.name AS Subtitle,
                           c.logo_url AS ImageUrl,
                           MIN(p.start_utc) AS SortKey
                    FROM programmes p
                    JOIN channel_epg_map m ON m.xmltv_id = p.channel_xmltv_id
                    JOIN channels c ON c.id = m.channel_id AND c.profile_id = @profileId
                    WHERE p.profile_id = @profileId
                      AND p.stop_utc > @nowUnix
                      AND p.start_utc < @horizon
                      AND p.title LIKE @pattern ESCAPE '\'
                    GROUP BY p.title, c.id
                    ORDER BY SortKey
                    LIMIT @limit
                    """,
                    new { profileId, pattern, nowUnix, horizon, limit = perGroupLimit }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false)).AsList();

                return new SearchResults(
                    channels.Select(r => r.ToHit()).ToList(),
                    movies.Select(r => r.ToHit()).ToList(),
                    series.Select(r => r.ToHit()).ToList(),
                    programmes.Select(r => r.ToHit()).ToList());
            }
        }, cancellationToken);
    }
}
