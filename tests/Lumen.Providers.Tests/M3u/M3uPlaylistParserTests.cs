using System.Text;
using FluentAssertions;
using Lumen.Providers.M3u;
using Lumen.Providers.Tests.Support;

namespace Lumen.Providers.Tests.M3u;

public sealed class M3uPlaylistParserTests
{
    private readonly M3uPlaylistParser _parser = new();

    private async Task<List<M3uEntry>> ParseFixtureAsync()
    {
        using var stream = FixtureFile.OpenRead("messy-playlist.m3u");
        var entries = new List<M3uEntry>();
        await foreach (var entry in _parser.ParseAsync(stream))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public async Task Parses_AllEntries_FromMessyPlaylist()
    {
        var entries = await ParseFixtureAsync();
        entries.Should().HaveCount(9);
    }

    [Fact]
    public async Task Parses_QuotedAttributes()
    {
        var entries = await ParseFixtureAsync();
        var first = entries[0];

        first.Title.Should().Be("BBC One FHD");
        first.TvgId.Should().Be("bbc1.uk");
        first.TvgName.Should().Be("BBC One");
        first.LogoUrl.Should().Be("http://logo/bbc1.png");
        first.GroupTitle.Should().Be("UK | News");
        first.Url.Should().Be("http://server/live/1.ts");
    }

    [Fact]
    public async Task Parses_UnquotedAttributes_AndEmptyValues()
    {
        var entries = await ParseFixtureAsync();
        var entry = entries[1];

        entry.Title.Should().Be("Sky Sports Main Event");
        entry.TvgId.Should().BeNull();
        entry.LogoUrl.Should().Be("http://logo/plain.png");
        entry.GroupTitle.Should().Be("Sports");
    }

    [Fact]
    public async Task ExtGrp_AppliesToFollowingEntries()
    {
        var entries = await ParseFixtureAsync();

        entries[2].GroupTitle.Should().Be("Documentaries");
        entries[2].Title.Should().Be("Nat Geo Wild");

        // Sticky until overridden by a group-title attribute.
        entries[3].GroupTitle.Should().Be("Documentaries");
        entries[4].GroupTitle.Should().Be("VOD | Movies");
    }

    [Fact]
    public async Task ExtVlcOpt_AppliesToNextEntryOnly()
    {
        var entries = await ParseFixtureAsync();

        entries[3].UserAgent.Should().Be("CustomAgent/1.0");
        entries[3].Referrer.Should().Be("http://portal.example.com");
        entries[4].UserAgent.Should().BeNull();
        entries[4].Referrer.Should().BeNull();
    }

    [Fact]
    public async Task TvgShift_ConvertsHoursToMinutes()
    {
        var entries = await ParseFixtureAsync();
        entries[3].TvgShiftMinutes.Should().Be(120);
    }

    [Fact]
    public async Task Title_PreservesCommasAfterAttributeSection()
    {
        var entries = await ParseFixtureAsync();
        entries[6].Title.Should().Be("Catchup Channel, The One With Commas");
        entries[6].CatchupType.Should().Be("shift");
    }

    [Fact]
    public async Task BareUrl_ProducesEntryWithDerivedTitle()
    {
        var entries = await ParseFixtureAsync();
        entries[7].Title.Should().Be("bare-url.ts");
        entries[7].Url.Should().Be("http://server/live/bare-url.ts");
    }

    [Fact]
    public async Task DuplicateUrls_AreTolerated()
    {
        var entries = await ParseFixtureAsync();
        entries.Count(e => e.Url == "http://server/live/1.ts").Should().Be(2);
    }

    [Fact]
    public async Task Handles_Bom_And_Lf_LineEndings()
    {
        var playlist = "﻿#EXTM3U\n#EXTINF:-1 tvg-id=\"a.b\",Channel A\nhttp://x/a.ts\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist));

        var entries = new List<M3uEntry>();
        await foreach (var entry in _parser.ParseAsync(stream))
        {
            entries.Add(entry);
        }

        entries.Should().ContainSingle();
        entries[0].TvgId.Should().Be("a.b");
        entries[0].Title.Should().Be("Channel A");
    }

    [Fact]
    public async Task MissingTitle_FallsBackToTvgName()
    {
        var playlist = "#EXTM3U\n#EXTINF:-1 tvg-name=\"Named\",\nhttp://x/a.ts\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist));

        var entries = new List<M3uEntry>();
        await foreach (var entry in _parser.ParseAsync(stream))
        {
            entries.Add(entry);
        }

        entries[0].Title.Should().Be("Named");
    }

    [Fact]
    public async Task LargePlaylist_StreamsWithoutMaterializing()
    {
        // 200k entries ≈ 30MB of playlist; the enumerator should yield them lazily.
        var builder = new StringBuilder("#EXTM3U\n");
        for (var i = 0; i < 200_000; i++)
        {
            builder.Append("#EXTINF:-1 tvg-id=\"ch").Append(i).Append("\",Channel ").Append(i).Append('\n');
            builder.Append("http://server/live/").Append(i).Append(".ts\n");
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
        var count = 0;
        await foreach (var _ in _parser.ParseAsync(stream))
        {
            count++;
        }

        count.Should().Be(200_000);
    }
}
