using Microsoft.Data.Sqlite;

namespace Lumen.Data;

/// <summary>Creates open connections to the Lumen SQLite database.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Absolute path of the database file.</summary>
    string DatabasePath { get; }

    /// <summary>Opens a new pooled connection. Callers own disposal.</summary>
    SqliteConnection Open();

    /// <summary>Opens a new pooled connection asynchronously. Callers own disposal.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}
