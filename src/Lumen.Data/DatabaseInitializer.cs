using Microsoft.Extensions.Logging;

namespace Lumen.Data;

/// <summary>
/// Prepares the database at startup: ensures the directory exists, applies durable pragmas
/// (WAL journaling), and runs pending migrations.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MigrationRunner _migrationRunner;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbConnectionFactory connectionFactory,
        MigrationRunner migrationRunner,
        ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
        _logger = logger;
    }

    /// <summary>Initializes the database. Returns the number of migrations applied.</summary>
    public async Task<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_connectionFactory.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragmas.ExecuteNonQuery();
            }

            var applied = _migrationRunner.Run(connection);
            _logger.LogInformation(
                "Database ready at {Path}; {Applied} migration(s) applied", _connectionFactory.DatabasePath, applied);
            return applied;
        }
    }
}
