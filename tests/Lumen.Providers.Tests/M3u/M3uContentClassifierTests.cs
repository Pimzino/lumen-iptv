using FluentAssertions;
using Lumen.Core.Models;
using Lumen.Providers.M3u;

namespace Lumen.Providers.Tests.M3u;

public sealed class M3uContentClassifierTests
{
    [Theory]
    [InlineData("BBC One", "http://s/1.ts", "UK News", ContentKind.Live)]
    [InlineData("Nat Geo", "http://s/2.m3u8", null, ContentKind.Live)]
    [InlineData("Channel No Extension", "http://s/stream/12345", null, ContentKind.Live)]
    [InlineData("The Long Voyage", "http://s/m/1.mp4", "VOD | Movies", ContentKind.Movie)]
    [InlineData("Old Film", "http://s/m/2.mkv", null, ContentKind.Movie)]
    [InlineData("Some Stream", "http://s/x.ts", "Films HD", ContentKind.Movie)]
    [InlineData("Breaking Code S01E05", "http://s/e.mkv", null, ContentKind.Series)]
    [InlineData("Breaking Code S01 E05", "http://s/e.mp4", "Whatever", ContentKind.Series)]
    [InlineData("Show 3x12", "http://s/e.avi", null, ContentKind.Series)]
    [InlineData("Some Episode", "http://s/e.mp4", "TV Series", ContentKind.Series)]
    [InlineData("News Feed", "http://s/live.ts?token=abc.mp4", null, ContentKind.Live)]
    public void Classify_UsesExtensionAndGroupHeuristics(
        string title, string url, string? group, ContentKind expected)
    {
        var entry = new M3uEntry { Title = title, Url = url, GroupTitle = group };
        M3uContentClassifier.Classify(entry).Should().Be(expected);
    }
}
