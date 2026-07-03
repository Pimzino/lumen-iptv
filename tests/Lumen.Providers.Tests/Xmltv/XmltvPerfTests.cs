using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using FluentAssertions;
using Lumen.Data;
using Lumen.Providers.Xmltv;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xmltv;

/// <summary>
/// Phase-2 gate: 1M-programme synthetic XMLTV must import into SQLite in under 60s with
/// bounded memory. Excluded from the default test run (Category=Perf); run via
/// <c>./build.ps1 -IncludePerf</c>.
/// </summary>
public sealed class XmltvPerfTests : IDisposable
{
    private const int ChannelCount = 200;
    private const int ProgrammesPerChannel = 5000;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lumen-perf", Guid.NewGuid().ToString("N"));

    public XmltvPerfTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Import_OneMillionProgrammes_Under60Seconds_BoundedMemory()
    {
        var xmlPath = Path.Combine(_directory, "big-guide.xml");
        GenerateSyntheticGuide(xmlPath);

        var dbPath = Path.Combine(_directory, "perf.db");
        var factory = new SqliteConnectionFactory(dbPath);
        var initializer = new DatabaseInitializer(
            factory, new MigrationRunner(NullLogger<MigrationRunner>.Instance),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using (var connection = await factory.OpenAsync())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                "INSERT INTO profiles (name, kind, created_utc) VALUES ('perf', 0, 0);";
            seed.ExecuteNonQuery();
        }

        var parser = new XmltvParser(NullLogger<XmltvParser>.Instance);
        var sinkFactory = new SqliteEpgImportSinkFactory(factory);

        var stopwatch = Stopwatch.StartNew();
        await using (var sink = sinkFactory.Create(profileId: 1))
        {
            using var stream = File.OpenRead(xmlPath);
            var result = await parser.ParseAsync(stream, sink, progress: null, CancellationToken.None);
            result.Programmes.Should().Be(ChannelCount * ProgrammesPerChannel);
            result.Channels.Should().Be(ChannelCount);
        }

        stopwatch.Stop();

        await using (var connection = await factory.OpenAsync())
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM programmes;";
            ((long)count.ExecuteScalar()!).Should().Be(ChannelCount * ProgrammesPerChannel);
        }

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(60), "the Phase-2 gate requires 1M programmes in under a minute");

        var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
        peakWorkingSet.Should().BeLessThan(
            300L * 1024 * 1024, "the Phase-2 gate requires bounded memory (streaming parse)");
    }

    private static void GenerateSyntheticGuide(string path)
    {
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = XmlWriter.Create(path, new XmlWriterSettings
        {
            Indent = false,
            Encoding = new UTF8Encoding(false),
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("tv");

        for (var c = 0; c < ChannelCount; c++)
        {
            writer.WriteStartElement("channel");
            writer.WriteAttributeString("id", $"synthetic.{c}");
            writer.WriteElementString("display-name", $"Synthetic Channel {c}");
            writer.WriteEndElement();
        }

        for (var c = 0; c < ChannelCount; c++)
        {
            var channelId = $"synthetic.{c}";
            for (var p = 0; p < ProgrammesPerChannel; p++)
            {
                var begin = start.AddMinutes(p * 30);
                var end = begin.AddMinutes(30);
                writer.WriteStartElement("programme");
                writer.WriteAttributeString("start", Stamp(begin));
                writer.WriteAttributeString("stop", Stamp(end));
                writer.WriteAttributeString("channel", channelId);
                writer.WriteElementString("title", $"Programme {c}-{p}");
                writer.WriteElementString("desc", "A synthetic entry generated for the performance gate.");
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
