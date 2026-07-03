using System.IO.Compression;
using FluentAssertions;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Xmltv;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xmltv;

/// <summary>In-memory sink capturing everything the parser produces.</summary>
public sealed class CollectingSink : IEpgImportSink
{
    public List<EpgChannel> Channels { get; } = [];

    public List<Programme> Programmes { get; } = [];

    public bool Begun { get; private set; }

    public bool Completed { get; private set; }

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        Begun = true;
        return Task.CompletedTask;
    }

    public ValueTask AddChannelAsync(EpgChannel channel, CancellationToken cancellationToken)
    {
        Channels.Add(channel);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddProgrammeAsync(Programme programme, CancellationToken cancellationToken)
    {
        Programmes.Add(programme);
        return ValueTask.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        Completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class XmltvParserTests
{
    private readonly XmltvParser _parser = new(NullLogger<XmltvParser>.Instance);

    private static long UtcUnix(int y, int mo, int d, int h, int mi) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task Parses_ChannelsAndProgrammes_SkippingMalformedEntries()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        var result = await _parser.ParseAsync(stream, sink, progress: null, CancellationToken.None);

        result.Channels.Should().Be(3);
        result.Programmes.Should().Be(5);
        result.Skipped.Should().Be(4); // empty channel id, bad start, stop<=start, missing title
        sink.Begun.Should().BeTrue();
        sink.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Channels_CarryDisplayNameAndIcon()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        await _parser.ParseAsync(stream, sink, null, CancellationToken.None);

        var bbc = sink.Channels.Single(c => c.XmltvId == "bbc1.uk");
        bbc.DisplayName.Should().Be("BBC One");
        bbc.IconUrl.Should().Be("http://logo/bbc1.png");
    }

    [Fact]
    public async Task TimezoneOffsets_NormalizeToSameUtcInstant()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        await _parser.ParseAsync(stream, sink, null, CancellationToken.None);

        var utc = sink.Programmes.Single(p => p.Title == "Evening News");
        var india = sink.Programmes.Single(p => p.Title == "Morning Raga");
        var la = sink.Programmes.Single(p => p.Title == "Noon Show");

        var expected = UtcUnix(2026, 7, 3, 20, 0);
        utc.StartUtc.Should().Be(expected);
        india.StartUtc.Should().Be(expected, "+05:30 must subtract the offset");
        la.StartUtc.Should().Be(expected, "-08:00 must add the offset");
    }

    [Fact]
    public async Task MissingOffset_IsTreatedAsUtc()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        await _parser.ParseAsync(stream, sink, null, CancellationToken.None);

        sink.Programmes.Single(p => p.Title == "No Offset Means UTC")
            .StartUtc.Should().Be(UtcUnix(2026, 7, 3, 22, 0));
    }

    [Fact]
    public async Task Programme_MetadataFieldsAreCaptured()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        await _parser.ParseAsync(stream, sink, null, CancellationToken.None);

        var news = sink.Programmes.Single(p => p.Title == "Evening News");
        news.Description.Should().Be("The day's events.");
        news.Category.Should().Be("News");
        news.EpisodeNumber.Should().Be("S01E05");
        news.IconUrl.Should().Be("http://img/ep.png");
        news.ChannelXmltvId.Should().Be("bbc1.uk");
    }

    [Fact]
    public async Task NestedElements_DoNotDerailParsing()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        await _parser.ParseAsync(stream, sink, null, CancellationToken.None);

        sink.Programmes.Should().Contain(p => p.Title == "Padded Title");
    }

    [Fact]
    public async Task GzipContent_IsDetectedAndDecompressed()
    {
        var raw = await File.ReadAllBytesAsync(FixtureFile.PathOf("guide-offsets.xml"));
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        compressed.Position = 0;
        await using var sink = new CollectingSink();

        var result = await _parser.ParseAsync(compressed, sink, null, CancellationToken.None);

        result.Programmes.Should().Be(5);
        result.Channels.Should().Be(3);
    }

    [Fact]
    public async Task Progress_IsReportedAtCompletion()
    {
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");
        var reports = new List<EpgImportProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await _parser.ParseAsync(stream, sink, progress, CancellationToken.None);

        reports.Should().NotBeEmpty();
        reports[^1].Channels.Should().Be(3);
        reports[^1].Programmes.Should().Be(5);
    }

    [Fact]
    public async Task Cancellation_StopsTheImport()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var sink = new CollectingSink();
        using var stream = FixtureFile.OpenRead("guide-offsets.xml");

        var act = () => _parser.ParseAsync(stream, sink, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class SynchronousProgress : IProgress<EpgImportProgress>
    {
        private readonly Action<EpgImportProgress> _handler;

        public SynchronousProgress(Action<EpgImportProgress> handler)
        {
            _handler = handler;
        }

        public void Report(EpgImportProgress value) => _handler(value);
    }
}
