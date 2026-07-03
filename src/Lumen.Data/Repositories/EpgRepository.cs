using Dapper;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.Data.Repositories;

/// <summary>SQLite-backed <see cref="IEpgRepository"/>.</summary>
public sealed class EpgRepository : IEpgRepository
{
    private const int IdChunkSize = 400; // stay under SQLite's parameter limit

    private readonly IDbConnectionFactory _connectionFactory;

    public EpgRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyDictionary<string, NowNext>> GetNowNextAsync(
        long profileId, IReadOnlyCollection<string> xmltvIds, long nowUnix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xmltvIds);
        return DbOffload.Run<IReadOnlyDictionary<string, NowNext>>(async () =>
        {
            var result = new Dictionary<string, NowNext>(xmltvIds.Count, StringComparer.OrdinalIgnoreCase);
            if (xmltvIds.Count == 0)
            {
                return result;
            }

            var horizon = nowUnix + 24 * 3600;
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                foreach (var chunk in Chunk(xmltvIds))
                {
                    var rows = await connection.QueryAsync<Programme>(new CommandDefinition(
                        """
                        SELECT * FROM (
                            SELECT p.*, ROW_NUMBER() OVER (
                                PARTITION BY p.channel_xmltv_id ORDER BY p.start_utc) AS rn
                            FROM programmes p
                            WHERE p.profile_id = @profileId
                              AND p.channel_xmltv_id IN @chunk
                              AND p.stop_utc > @nowUnix
                              AND p.start_utc < @horizon
                        ) WHERE rn <= 2
                        """,
                        new { profileId, chunk, nowUnix, horizon },
                        cancellationToken: cancellationToken)).ConfigureAwait(false);

                    foreach (var group in rows.GroupBy(p => p.ChannelXmltvId, StringComparer.OrdinalIgnoreCase))
                    {
                        Programme? now = null;
                        Programme? next = null;
                        foreach (var programme in group.OrderBy(p => p.StartUtc))
                        {
                            if (programme.StartUtc <= nowUnix && nowUnix < programme.StopUtc)
                            {
                                now = programme;
                            }
                            else if (programme.StartUtc > nowUnix && next is null)
                            {
                                next = programme;
                            }
                        }

                        result[group.Key] = new NowNext(now, next);
                    }
                }
            }

            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<Programme>> GetProgrammesAsync(
        long profileId,
        IReadOnlyCollection<string> xmltvIds,
        long fromUnix,
        long toUnix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xmltvIds);
        return DbOffload.Run<IReadOnlyList<Programme>>(async () =>
        {
            if (xmltvIds.Count == 0)
            {
                return [];
            }

            var result = new List<Programme>();
            var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                foreach (var chunk in Chunk(xmltvIds))
                {
                    var rows = await connection.QueryAsync<Programme>(new CommandDefinition(
                        """
                        SELECT * FROM programmes
                        WHERE profile_id = @profileId
                          AND channel_xmltv_id IN @chunk
                          AND start_utc < @toUnix AND stop_utc > @fromUnix
                        ORDER BY channel_xmltv_id, start_utc
                        """,
                        new { profileId, chunk, fromUnix, toUnix },
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                    result.AddRange(rows);
                }
            }

            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<EpgChannel>> GetEpgChannelsAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<EpgChannel>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<EpgChannel>(new CommandDefinition(
                "SELECT * FROM epg_channels WHERE profile_id = @profileId ORDER BY display_name",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task<int> PurgeProgrammesBeforeAsync(long profileId, long cutoffUnix, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM programmes WHERE profile_id = @profileId AND stop_utc < @cutoffUnix",
                new { profileId, cutoffUnix }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task<IReadOnlyList<ChannelEpgMapping>> GetMappingsAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run<IReadOnlyList<ChannelEpgMapping>>(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<ChannelEpgMapping>(new CommandDefinition(
                """
                SELECT m.* FROM channel_epg_map m
                JOIN channels c ON c.id = m.channel_id
                WHERE c.profile_id = @profileId
                """,
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
    }, cancellationToken);

    public Task SetManualMappingAsync(long channelId, string? xmltvId, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(xmltvId))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM channel_epg_map WHERE channel_id = @channelId",
                    new { channelId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO channel_epg_map (channel_id, xmltv_id, is_manual)
                    VALUES (@channelId, @xmltvId, 1)
                    ON CONFLICT (channel_id) DO UPDATE SET xmltv_id = excluded.xmltv_id, is_manual = 1
                    """,
                    new { channelId, xmltvId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }, cancellationToken);

    public Task<int> ApplyAutoMappingsAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var channels = (await connection.QueryAsync<Channel>(new CommandDefinition(
                "SELECT * FROM channels WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

            var epgChannels = (await connection.QueryAsync<EpgChannel>(new CommandDefinition(
                "SELECT * FROM epg_channels WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

            var manualIds = (await connection.QueryAsync<long>(new CommandDefinition(
                """
                SELECT m.channel_id FROM channel_epg_map m
                JOIN channels c ON c.id = m.channel_id
                WHERE c.profile_id = @profileId AND m.is_manual = 1
                """,
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();

            var candidates = channels.Where(c => !manualIds.Contains(c.Id)).ToList();
            var mappings = EpgMatcher.Match(candidates, epgChannels);

            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                """
                DELETE FROM channel_epg_map
                WHERE is_manual = 0
                  AND channel_id IN (SELECT id FROM channels WHERE profile_id = @profileId)
                """,
                new { profileId }, transaction).ConfigureAwait(false);

            foreach (var mapping in mappings)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO channel_epg_map (channel_id, xmltv_id, is_manual)
                    VALUES (@ChannelId, @XmltvId, 0)
                    ON CONFLICT (channel_id) DO NOTHING
                    """,
                    mapping, transaction).ConfigureAwait(false);
            }

            transaction.Commit();
            return mappings.Count;
        }
    }, cancellationToken);

    public Task<(long Channels, long Programmes)> GetCountsAsync(long profileId, CancellationToken cancellationToken) =>
        DbOffload.Run(async () =>
    {
        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var channels = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM epg_channels WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var programmes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM programmes WHERE profile_id = @profileId",
                new { profileId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return (channels, programmes);
        }
    }, cancellationToken);

    private static IEnumerable<List<string>> Chunk(IReadOnlyCollection<string> ids)
    {
        var chunk = new List<string>(Math.Min(ids.Count, IdChunkSize));
        foreach (var id in ids)
        {
            chunk.Add(id);
            if (chunk.Count == IdChunkSize)
            {
                yield return chunk;
                chunk = new List<string>(IdChunkSize);
            }
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }
}
