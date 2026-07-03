using System.Text;
using FluentAssertions;
using Lumen.Providers.M3u;

namespace Lumen.Providers.Tests.M3u;

public sealed class M3uEdgeCaseTests
{
    private readonly M3uPlaylistParser _parser = new();

    private async Task<List<M3uEntry>> ParseAsync(string playlist)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist));
        var entries = new List<M3uEntry>();
        await foreach (var entry in _parser.ParseAsync(stream))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public async Task ExtVlcOpt_AlternateRefererSpelling_IsAccepted()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTVLCOPT:http-referer=http://r\n#EXTINF:-1,Ch\nhttp://x/a.ts\n");
        entries[0].Referrer.Should().Be("http://r");
    }

    [Fact]
    public async Task ExtVlcOpt_WithoutEquals_IsIgnored()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTVLCOPT:garbage\n#EXTINF:-1,Ch\nhttp://x/a.ts\n");
        entries[0].UserAgent.Should().BeNull();
    }

    [Fact]
    public async Task ExtVlcOpt_QuotedValue_IsUnquoted()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTVLCOPT:http-user-agent=\"Quoted Agent\"\n#EXTINF:-1,Ch\nhttp://x/a.ts\n");
        entries[0].UserAgent.Should().Be("Quoted Agent");
    }

    [Fact]
    public async Task NegativeTvgShift_ConvertsToNegativeMinutes()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTINF:-1 tvg-shift=\"-1.5\",Ch\nhttp://x/a.ts\n");
        entries[0].TvgShiftMinutes.Should().Be(-90);
    }

    [Fact]
    public async Task GarbageTvgShift_DefaultsToZero()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTINF:-1 tvg-shift=\"abc\",Ch\nhttp://x/a.ts\n");
        entries[0].TvgShiftMinutes.Should().Be(0);
    }

    [Fact]
    public async Task UnterminatedQuote_DoesNotCrash()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTINF:-1 tvg-name=\"Unterminated,Ch\nhttp://x/a.ts\n");
        entries.Should().ContainSingle();
        entries[0].Url.Should().Be("http://x/a.ts");
    }

    [Fact]
    public async Task BareUrl_WithQueryString_DerivesTitleFromLastSegment()
    {
        var entries = await ParseAsync("#EXTM3U\nhttp://x/stream/abc%20def.ts?token=1\n");
        entries[0].Title.Should().Be("abc def.ts");
    }

    [Fact]
    public async Task BareUrl_WithTrailingSlash_StillDerivesTitle()
    {
        var entries = await ParseAsync("#EXTM3U\nhttp://host/path/name/\n");
        entries[0].Title.Should().Be("name");
    }

    [Fact]
    public async Task EmptyExtGrp_ClearsStickyGroup()
    {
        var entries = await ParseAsync(
            "#EXTM3U\n#EXTGRP:Docs\n#EXTINF:-1,A\nhttp://x/a.ts\n#EXTGRP:\n#EXTINF:-1,B\nhttp://x/b.ts\n");
        entries[0].GroupTitle.Should().Be("Docs");
        entries[1].GroupTitle.Should().BeNull();
    }

    [Fact]
    public async Task DurationWithoutAttributes_ParsesTitle()
    {
        var entries = await ParseAsync("#EXTM3U\n#EXTINF:12.5,Short Clip\nhttp://x/clip.mp4\n");
        entries[0].DurationSeconds.Should().Be(12.5);
        entries[0].Title.Should().Be("Short Clip");
    }
}
