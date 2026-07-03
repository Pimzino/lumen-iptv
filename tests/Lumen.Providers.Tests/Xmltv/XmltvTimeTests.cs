using FluentAssertions;
using Lumen.Providers.Xmltv;

namespace Lumen.Providers.Tests.Xmltv;

public sealed class XmltvTimeTests
{
    private static long Unix(int y, int mo, int d, int h, int mi, int s, TimeSpan offset) =>
        new DateTimeOffset(y, mo, d, h, mi, s, offset).ToUnixTimeSeconds();

    [Fact]
    public void Parses_UtcOffset()
    {
        XmltvTime.TryParse("20260703200000 +0000", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_IndiaHalfHourOffset()
    {
        XmltvTime.TryParse("20260704013000 +0530", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 4, 1, 30, 0, TimeSpan.FromMinutes(330)));

        // +05:30 at 01:30 local is 20:00 UTC the previous day.
        unix.Should().Be(Unix(2026, 7, 3, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_NegativeOffset()
    {
        XmltvTime.TryParse("20260703120000 -0800", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 12, 0, 0, TimeSpan.FromHours(-8)));
    }

    [Fact]
    public void MissingOffset_DefaultsToUtc()
    {
        XmltvTime.TryParse("20260703220000", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 22, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("20260703200000 UTC")]
    [InlineData("20260703200000 GMT")]
    [InlineData("20260703200000 Z")]
    public void NamedUtcSuffixes_AreAccepted(string input)
    {
        XmltvTime.TryParse(input, out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_ColonSeparatedOffset()
    {
        XmltvTime.TryParse("20260703200000 +05:30", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 20, 0, 0, TimeSpan.FromMinutes(330)));
    }

    [Fact]
    public void Parses_MinutePrecision()
    {
        XmltvTime.TryParse("202607032015 +0000", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 20, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_DateOnly()
    {
        XmltvTime.TryParse("20260703", out var unix).Should().BeTrue();
        unix.Should().Be(Unix(2026, 7, 3, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026")]
    [InlineData("20261303120000 +0000")] // month 13
    [InlineData("20260230120000 +0000")] // February 30
    [InlineData("20260703120000 %0800")]
    [InlineData("20260703120000 +99999")]
    public void RejectsGarbage(string input) =>
        XmltvTime.TryParse(input, out _).Should().BeFalse();
}
