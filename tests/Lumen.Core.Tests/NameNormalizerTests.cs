using FluentAssertions;
using Lumen.Core;

namespace Lumen.Core.Tests;

public sealed class NameNormalizerTests
{
    [Theory]
    [InlineData("BBC One HD", "bbc one")]
    [InlineData("BBC ONE FHD", "bbc one")]
    [InlineData("bbc one", "bbc one")]
    [InlineData("UK: BBC One FHD", "bbc one")]
    [InlineData("UK | Sky Sports 4K", "sky sports")]
    [InlineData("Canal+ Décalé", "canal decale")]
    [InlineData("  Sky   Sports  Main Event ", "sky sports main event")]
    [InlineData("TNT-Sports.1", "tnt sports 1")]
    [InlineData("Discovery HEVC H265", "discovery")]
    [InlineData("HD", "hd")] // stripping everything falls back to the folded text
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ProducesStableKeys(string? input, string expected) =>
        NameNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_TreatsQualityVariantsAsEqual()
    {
        var variants = new[] { "BBC One", "BBC One HD", "UK: BBC ONE FHD", "bbc-one 4K" };
        variants.Select(NameNormalizer.Normalize).Distinct().Should().ContainSingle();
    }
}
