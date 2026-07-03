using FluentAssertions;
using Lumen.Core;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public sealed class EpgMatcherTests
{
    private static Channel Ch(long id, string name, string? epgId = null) =>
        new() { Id = id, Name = name, EpgChannelId = epgId };

    private static EpgChannel Epg(string xmltvId, string? displayName) =>
        new() { XmltvId = xmltvId, DisplayName = displayName };

    [Fact]
    public void TvgId_ExactMatch_WinsOverName()
    {
        var channels = new[] { Ch(1, "Completely Different Name", "bbc1.uk") };
        var epg = new[] { Epg("bbc1.uk", "BBC One"), Epg("other.uk", "Completely Different Name") };

        var mappings = EpgMatcher.Match(channels, epg);

        mappings.Should().ContainSingle().Which.XmltvId.Should().Be("bbc1.uk");
    }

    [Fact]
    public void TvgId_MatchIsCaseInsensitive()
    {
        var channels = new[] { Ch(1, "x", "BBC1.UK") };
        var epg = new[] { Epg("bbc1.uk", "BBC One") };

        EpgMatcher.Match(channels, epg).Should().ContainSingle()
            .Which.XmltvId.Should().Be("bbc1.uk");
    }

    [Fact]
    public void NormalizedName_MatchesDespiteQualitySuffixAndPrefix()
    {
        var channels = new[] { Ch(1, "UK: BBC One FHD") };
        var epg = new[] { Epg("bbc1.uk", "BBC One") };

        EpgMatcher.Match(channels, epg).Should().ContainSingle()
            .Which.XmltvId.Should().Be("bbc1.uk");
    }

    [Fact]
    public void DottedXmltvId_ActsAsNameFallback()
    {
        var channels = new[] { Ch(1, "Sky Sports Main Event HD") };
        var epg = new[] { Epg("sky.sports.main.event", null) };

        EpgMatcher.Match(channels, epg).Should().ContainSingle()
            .Which.XmltvId.Should().Be("sky.sports.main.event");
    }

    [Fact]
    public void UnmatchedChannels_AreOmitted()
    {
        var channels = new[] { Ch(1, "Obscure Channel 999") };
        var epg = new[] { Epg("bbc1.uk", "BBC One") };

        EpgMatcher.Match(channels, epg).Should().BeEmpty();
    }

    [Fact]
    public void ProducesMappingPerChannel_NotPerEpgEntry()
    {
        var channels = new[] { Ch(1, "BBC One HD"), Ch(2, "BBC One FHD") };
        var epg = new[] { Epg("bbc1.uk", "BBC One") };

        var mappings = EpgMatcher.Match(channels, epg);

        mappings.Should().HaveCount(2);
        mappings.Select(m => m.ChannelId).Should().BeEquivalentTo([1L, 2L]);
        mappings.Should().OnlyContain(m => m.XmltvId == "bbc1.uk" && !m.IsManual);
    }
}
