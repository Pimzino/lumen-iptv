using Dapper;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

/// <summary>Default <see cref="IDbConnectionFactory"/> backed by Microsoft.Data.Sqlite pooling.</summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    static SqliteConnectionFactory()
    {
        // Schema convention: snake_case columns (profile_id) map onto PascalCase
        // properties (ProfileId). The factory is the single entry point to the
        // database, so configuring Dapper here covers every consumer.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 15,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
