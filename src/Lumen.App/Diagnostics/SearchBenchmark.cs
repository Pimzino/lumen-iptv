using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Lumen.Core.Abstractions;
using Lumen.Data;
using Lumen.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Phase-7 gate: seeds a large synthetic catalog (10k channels, 20k VOD, 200k programmes) into a
/// throwaway database and confirms grouped search returns in under 150ms on a warm cache.
/// </summary>
public static class SearchBenchmark
{
    public static async Task<int> RunAsync(string outFile)
    {
        var report = new StringBuilder();
        var directory = Path.Combine(Path.GetTempPath(), "lumen-searchbench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "search.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var initializer = new DatabaseInitializer(
                factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
                NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            await SeedAsync(factory, report);

            var search = new SearchRepository(factory);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Warm the cache + connection pool.
            await search.SearchAsync(1, "news", now, 20, CancellationToken.None);

            var samples = new List<double>();
            foreach (var term in new[] { "news", "sport", "movie 123", "show 4", "channel 900", "premier" })
            {
                var sw = Stopwatch.StartNew();
                var results = await search.SearchAsync(1, term, now, 20, CancellationToken.None);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                report.AppendLine($"query='{term}' hits={results.TotalCount} ms={sw.Elapsed.TotalMilliseconds:F1}");
            }

            samples.Sort();
            var median = samples[samples.Count / 2];
            var max = samples[^1];
            report.AppendLine($"medianMs={median:F1} maxMs={max:F1}");

            var pass = max < 150;
            report.AppendLine(pass ? "SEARCH-RESULT=PASS" : "SEARCH-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Search benchmark failed");
            report.AppendLine($"SEARCH-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task SeedAsync(SqliteConnectionFactory factory, StringBuilder report)
    {
        var connection = await factory.OpenAsync();
        await using (connection.ConfigureAwait(false))
        {
            using var seedProfile = connection.CreateCommand();
            seedProfile.CommandText = "INSERT INTO profiles (name, kind, created_utc) VALUES ('bench', 0, 0);";
            seedProfile.ExecuteNonQuery();

            using var transaction = connection.BeginTransaction();

            // 10k channels
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO channels (profile_id, name, added_utc) VALUES (1, $name, 0);";
                var pName = insert.Parameters.Add("$name", SqliteType.Text);
                for (var i = 0; i < 10_000; i++)
                {
                    pName.Value = $"Channel {i} {Category(i)}";
                    insert.ExecuteNonQuery();
                }
            }

            // 20k VOD (movies + series)
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO vod_items (profile_id, kind, provider_item_id, name) VALUES (1, $kind, $pid, $name);";
                var pKind = insert.Parameters.Add("$kind", SqliteType.Integer);
                var pPid = insert.Parameters.Add("$pid", SqliteType.Text);
                var pName = insert.Parameters.Add("$name", SqliteType.Text);
                for (var i = 0; i < 20_000; i++)
                {
                    pKind.Value = i % 2 == 0 ? 1 : 2;
                    pPid.Value = $"v{i}";
                    pName.Value = $"Movie {i} {Category(i)}";
                    insert.ExecuteNonQuery();
                }
            }

            // 200k programmes, mapped to the first 500 channels
            using (var mapInsert = connection.CreateCommand())
            {
                mapInsert.Transaction = transaction;
                mapInsert.CommandText =
                    "INSERT INTO channel_epg_map (channel_id, xmltv_id, is_manual) VALUES ($cid, $xid, 0);";
                var pCid = mapInsert.Parameters.Add("$cid", SqliteType.Integer);
                var pXid = mapInsert.Parameters.Add("$xid", SqliteType.Text);
                for (var i = 1; i <= 500; i++)
                {
                    pCid.Value = i;
                    pXid.Value = $"x{i}";
                    mapInsert.ExecuteNonQuery();
                }
            }

            var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO programmes (profile_id, channel_xmltv_id, start_utc, stop_utc, title) " +
                    "VALUES (1, $xid, $start, $stop, $title);";
                var pXid = insert.Parameters.Add("$xid", SqliteType.Text);
                var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
                var pStop = insert.Parameters.Add("$stop", SqliteType.Integer);
                var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
                for (var i = 0; i < 200_000; i++)
                {
                    pXid.Value = $"x{(i % 500) + 1}";
                    pStart.Value = future + i * 60L;
                    pStop.Value = future + i * 60L + 1800;
                    pTitle.Value = $"Show {i} {Category(i)}";
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            report.AppendLine("seeded channels=10000 vod=20000 programmes=200000");
        }
    }

    private static string Category(int i) => (i % 5) switch
    {
        0 => "News",
        1 => "Sport",
        2 => "Movie Premier",
        3 => "Kids",
        _ => "Documentary",
    } + " " + (i % 1000).ToString(CultureInfo.InvariantCulture);
}
