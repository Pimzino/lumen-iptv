using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

/// <summary>
/// Bulk EPG writer: replaces the profile's EPG, inserting with prepared commands in
/// transactions of 5,000 rows. Import of a million programmes runs in constant memory.
/// </summary>
public sealed class SqliteEpgImportSink : IEpgImportSink
{
    private const int BatchSize = 5000;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly long _profileId;

    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SqliteCommand? _channelInsert;
    private SqliteCommand? _programmeInsert;
    private int _batchedRows;
    private bool _completed;

    internal SqliteEpgImportSink(IDbConnectionFactory connectionFactory, long profileId)
    {
        _connectionFactory = connectionFactory;
        _profileId = profileId;
    }

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        _connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        _transaction = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = _transaction;
            clear.CommandText =
                "DELETE FROM programmes WHERE profile_id = $profile;" +
                "DELETE FROM epg_channels WHERE profile_id = $profile;";
            clear.Parameters.AddWithValue("$profile", _profileId);
            clear.ExecuteNonQuery();
        }

        _channelInsert = _connection.CreateCommand();
        _channelInsert.Transaction = _transaction;
        _channelInsert.CommandText =
            "INSERT OR IGNORE INTO epg_channels (profile_id, xmltv_id, display_name, icon_url) " +
            "VALUES ($profile, $id, $name, $icon);";
        _channelInsert.Parameters.AddWithValue("$profile", _profileId);
        _channelInsert.Parameters.Add("$id", SqliteType.Text);
        _channelInsert.Parameters.Add("$name", SqliteType.Text);
        _channelInsert.Parameters.Add("$icon", SqliteType.Text);

        _programmeInsert = _connection.CreateCommand();
        _programmeInsert.Transaction = _transaction;
        _programmeInsert.CommandText =
            "INSERT INTO programmes (profile_id, channel_xmltv_id, start_utc, stop_utc, title, description, category, episode_number, icon_url) " +
            "VALUES ($profile, $channel, $start, $stop, $title, $desc, $category, $episode, $icon);";
        _programmeInsert.Parameters.AddWithValue("$profile", _profileId);
        _programmeInsert.Parameters.Add("$channel", SqliteType.Text);
        _programmeInsert.Parameters.Add("$start", SqliteType.Integer);
        _programmeInsert.Parameters.Add("$stop", SqliteType.Integer);
        _programmeInsert.Parameters.Add("$title", SqliteType.Text);
        _programmeInsert.Parameters.Add("$desc", SqliteType.Text);
        _programmeInsert.Parameters.Add("$category", SqliteType.Text);
        _programmeInsert.Parameters.Add("$episode", SqliteType.Text);
        _programmeInsert.Parameters.Add("$icon", SqliteType.Text);
    }

    public ValueTask AddChannelAsync(EpgChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var command = _channelInsert ?? throw NotBegun();
        command.Parameters["$id"].Value = channel.XmltvId;
        command.Parameters["$name"].Value = (object?)channel.DisplayName ?? DBNull.Value;
        command.Parameters["$icon"].Value = (object?)channel.IconUrl ?? DBNull.Value;
        command.ExecuteNonQuery();
        BumpBatch();
        return ValueTask.CompletedTask;
    }

    public ValueTask AddProgrammeAsync(Programme programme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(programme);
        var command = _programmeInsert ?? throw NotBegun();
        command.Parameters["$channel"].Value = programme.ChannelXmltvId;
        command.Parameters["$start"].Value = programme.StartUtc;
        command.Parameters["$stop"].Value = programme.StopUtc;
        command.Parameters["$title"].Value = programme.Title;
        command.Parameters["$desc"].Value = (object?)programme.Description ?? DBNull.Value;
        command.Parameters["$category"].Value = (object?)programme.Category ?? DBNull.Value;
        command.Parameters["$episode"].Value = (object?)programme.EpisodeNumber ?? DBNull.Value;
        command.Parameters["$icon"].Value = (object?)programme.IconUrl ?? DBNull.Value;
        command.ExecuteNonQuery();
        BumpBatch();
        return ValueTask.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
        }

        _completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_completed && _transaction is not null)
        {
            // Import aborted — roll back the partial batch, leaving the previous EPG intact
            // only if the very first batch failed; later batches were already committed.
            _transaction.Rollback();
        }

        _transaction?.Dispose();
        _channelInsert?.Dispose();
        _programmeInsert?.Dispose();
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void BumpBatch()
    {
        if (++_batchedRows < BatchSize)
        {
            return;
        }

        _batchedRows = 0;
        _transaction!.Commit();
        _transaction.Dispose();
        _transaction = _connection!.BeginTransaction();
        _channelInsert!.Transaction = _transaction;
        _programmeInsert!.Transaction = _transaction;
    }

    private static InvalidOperationException NotBegun() =>
        new("BeginAsync must be called before adding entities.");
}

/// <summary>Creates per-profile sinks bound to the app database.</summary>
public sealed class SqliteEpgImportSinkFactory : IEpgImportSinkFactory
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SqliteEpgImportSinkFactory(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IEpgImportSink Create(long profileId) => new SqliteEpgImportSink(_connectionFactory, profileId);
}
