using FluentAssertions;
using Lumen.Providers.Xtream;

namespace Lumen.Providers.Tests;

public sealed class XtreamTimeshiftTests
{
    [Fact]
    public void Timeshift_BuildsCatchupUrl()
    {
        var url = XtreamUrls.Timeshift(
            "example.com:8080", "user", "pass", "42",
            new DateTime(2026, 7, 4, 20, 5, 0), durationMinutes: 95);

        url.AbsoluteUri.Should().Be("http://example.com:8080/timeshift/user/pass/95/2026-07-04:20-05/42.ts");
    }

    [Fact]
    public void Timeshift_RejectsNonPositiveDuration()
    {
        var act = () => XtreamUrls.Timeshift("example.com", "u", "p", "1", DateTime.UnixEpoch, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToServerLocal_UsesIanaTimezone_WithDstForPastInstants()
    {
        var info = new XtreamServerInfo { Timezone = "Europe/London" };

        // Summer instant: BST is UTC+1.
        XtreamServerTime.ToServerLocal(
                new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero), info)
            .Should().Be(new DateTime(2026, 7, 4, 13, 0, 0));

        // Winter instant through the same zone: GMT is UTC+0.
        XtreamServerTime.ToServerLocal(
                new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), info)
            .Should().Be(new DateTime(2026, 1, 15, 12, 0, 0));
    }

    [Fact]
    public void ToServerLocal_FallsBackToClockPairOffset_WhenTimezoneUnusable()
    {
        var utcNow = new DateTimeOffset(2026, 7, 4, 12, 0, 10, TimeSpan.Zero);
        var info = new XtreamServerInfo
        {
            Timezone = "Not/AZone",
            TimestampNow = utcNow.ToUnixTimeSeconds(),
            TimeNow = "2026-07-04 15:00:11", // panel clock reads UTC+3 (one tick of skew)
        };

        XtreamServerTime.ToServerLocal(new DateTimeOffset(2026, 7, 4, 18, 30, 0, TimeSpan.Zero), info)
            .Should().Be(new DateTime(2026, 7, 4, 21, 30, 0));
    }

    [Fact]
    public void ToServerLocal_DefaultsToUtc_WithoutServerInfo()
    {
        var utc = new DateTimeOffset(2026, 7, 4, 18, 30, 0, TimeSpan.Zero);
        XtreamServerTime.ToServerLocal(utc, null).Should().Be(utc.UtcDateTime);
    }

    [Fact]
    public void ToChannel_MapsArchiveFlags()
    {
        var dto = new XtreamLiveStream
        {
            StreamId = "7",
            Name = "Channel 7",
            HasArchive = true,
            ArchiveDurationDays = 5,
        };

        var channel = XtreamMapper.ToChannel(dto, profileId: 1,
            new Dictionary<string, long>(), nowUnix: 0);

        channel.Should().NotBeNull();
        channel!.HasArchive.Should().BeTrue();
        channel.ArchiveDays.Should().Be(5);
    }

    [Fact]
    public void ToChannel_IgnoresArchiveDuration_WhenArchiveDisabled()
    {
        var dto = new XtreamLiveStream
        {
            StreamId = "7",
            Name = "Channel 7",
            HasArchive = false,
            ArchiveDurationDays = 5,
        };

        var channel = XtreamMapper.ToChannel(dto, profileId: 1,
            new Dictionary<string, long>(), nowUnix: 0);

        channel!.HasArchive.Should().BeFalse();
        channel.ArchiveDays.Should().Be(0);
    }
}
