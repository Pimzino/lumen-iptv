using FluentAssertions;
using Lumen.Core.Updates;

namespace Lumen.Core.Tests.Updates;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.1", 0, 1, 1)]
    [InlineData("0.1.1", 0, 1, 1)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("0.1.1.0", 0, 1, 1)]
    [InlineData("1.2.3+build.7", 1, 2, 3)]
    public void TryParse_ReadsCoreComponents(string text, int major, int minor, int patch)
    {
        ReleaseVersion.TryParse(text, out var version).Should().BeTrue();
        version!.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    public void TryParse_RejectsMalformedInput(string? text)
    {
        ReleaseVersion.TryParse(text, out var version).Should().BeFalse();
        version.Should().BeNull();
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.1")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("1.3.0", "1.2.9")]
    [InlineData("v0.1.2", "v0.1.1")]
    public void GreaterThan_WhenCoreIsNewer(string newer, string older)
    {
        ReleaseVersion.Parse(newer).Should().BeGreaterThan(ReleaseVersion.Parse(older));
        (ReleaseVersion.Parse(newer) > ReleaseVersion.Parse(older)).Should().BeTrue();
    }

    [Fact]
    public void FinalRelease_OutranksSameCorePreRelease()
    {
        var final = ReleaseVersion.Parse("0.2.0");
        var beta = ReleaseVersion.Parse("0.2.0-beta");

        (final > beta).Should().BeTrue();
        (beta < final).Should().BeTrue();
        final.IsPreRelease.Should().BeFalse();
        beta.IsPreRelease.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]      // larger identifier set wins
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")] // numeric < alphanumeric
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]    // ordinal identifier ordering
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]     // numeric identifiers compare by value
    public void PreRelease_OrdersBySemVerPrecedence(string lower, string higher)
    {
        (ReleaseVersion.Parse(lower) < ReleaseVersion.Parse(higher)).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3", "1.2.3+meta")]
    [InlineData("1.2", "1.2.0")]
    public void Equality_IgnoresCosmeticDifferences(string a, string b)
    {
        var left = ReleaseVersion.Parse(a);
        var right = ReleaseVersion.Parse(b);

        (left == right).Should().BeTrue();
        left.CompareTo(right).Should().Be(0);
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    [Fact]
    public void CurrentAssemblyStyleVersion_IsOlderThanNextRelease()
    {
        // The app's assembly version is a 4-part System.Version string; releases are 3-part tags.
        var current = ReleaseVersion.Parse("0.1.1.0");
        var next = ReleaseVersion.Parse("v0.1.2");

        (next > current).Should().BeTrue();
    }
}
