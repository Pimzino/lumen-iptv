using Dapper;
using FluentAssertions;
using Lumen.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Core.Tests.Data;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_directory, "lumen-test.db");

    [Fact]
    public async Task InitializeAsync_CreatesAllCoreTables()
    {
        var initializer = CreateInitializer(out var factory);

        await initializer.InitializeAsync();

        await using var connection = await factory.OpenAsync();
        var tables = (await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table'")).ToList();

        tables.Should().Contain(new[]
        {
            "schema_migrations", "profiles", "categories", "channels", "epg_channels",
            "programmes", "channel_epg_map", "favorites", "watch_history", "settings",
        });
    }

    [Fact]
    public async Task InitializeAsync_SecondRunAppliesNothing()
    {
        var initializer = CreateInitializer(out _);

        var first = await initializer.InitializeAsync();
        var second = await initializer.InitializeAsync();

        first.Should().BeGreaterThan(0);
        second.Should().Be(0);
    }

    [Fact]
    public async Task InitializeAsync_EnablesWalJournaling()
    {
        var initializer = CreateInitializer(out var factory);

        await initializer.InitializeAsync();

        await using var connection = await factory.OpenAsync();
        var mode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;");
        mode.Should().Be("wal");
    }

    [Fact]
    public async Task InitializeAsync_RecordsAppliedVersions()
    {
        var initializer = CreateInitializer(out var factory);

        await initializer.InitializeAsync();

        await using var connection = await factory.OpenAsync();
        var versions = (await connection.QueryAsync<long>(
            "SELECT version FROM schema_migrations ORDER BY version")).ToList();
        versions.Should().NotBeEmpty();
        versions[0].Should().Be(1);
        versions.Should().BeInAscendingOrder();
    }

    [Fact]
    public void MigrationScripts_HaveUniqueVersionsStartingAtOne()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        scripts.Should().NotBeEmpty();
        scripts.Select(s => s.Version).Should().OnlyHaveUniqueItems();
        scripts.Min(s => s.Version).Should().Be(1);
    }

    private DatabaseInitializer CreateInitializer(out SqliteConnectionFactory factory)
    {
        factory = new SqliteConnectionFactory(DbPath);
        return new DatabaseInitializer(
            factory,
            new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
