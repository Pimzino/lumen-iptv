using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lumen.Data;

/// <summary>
/// Applies versioned SQL scripts embedded under <c>Migrations/</c>. Scripts are named
/// <c>NNNN_description.sql</c> and run in ascending order inside individual transactions.
/// Applied versions are recorded in <c>schema_migrations</c>.
/// </summary>
public sealed class MigrationRunner
{
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(ILogger<MigrationRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>Applies all pending migrations. Returns the number applied.</summary>
    public int Run(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations (" +
                "version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc INTEGER NOT NULL);";
            create.ExecuteNonQuery();
        }

        long current;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT IFNULL(MAX(version), 0) FROM schema_migrations;";
            current = (long)(query.ExecuteScalar() ?? 0L);
        }

        var applied = 0;
        foreach (var script in DiscoverScripts().Where(s => s.Version > current).OrderBy(s => s.Version))
        {
            _logger.LogInformation("Applying migration {Version} ({Name})", script.Version, script.Name);
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = script.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_utc) VALUES ($version, $name, $applied);";
                record.Parameters.AddWithValue("$version", script.Version);
                record.Parameters.AddWithValue("$name", script.Name);
                record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return applied;
    }

    /// <summary>Reads all embedded migration scripts, unordered.</summary>
    internal static IReadOnlyList<MigrationScript> DiscoverScripts()
    {
        const string marker = ".Migrations.";
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var markerIndex = resource.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var fileName = resource[(markerIndex + marker.Length)..];
            var underscore = fileName.IndexOf('_', StringComparison.Ordinal);
            if (underscore <= 0 ||
                !long.TryParse(fileName[..underscore], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException(
                    $"Migration resource '{resource}' must be named '<version>_<description>.sql'.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing migration resource stream '{resource}'.");
            using var reader = new StreamReader(stream);
            scripts.Add(new MigrationScript(version, fileName, reader.ReadToEnd()));
        }

        return scripts;
    }
}

internal sealed record MigrationScript(long Version, string Name, string Sql);
