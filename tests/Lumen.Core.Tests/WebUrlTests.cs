using FluentAssertions;
using Lumen.Core;

namespace Lumen.Core.Tests;

public sealed class WebUrlTests
{
    [Theory]
    [InlineData("http://img.example.com/poster.jpg", true)]
    [InlineData("https://image.tmdb.org/t/p/w600/abc.jpg", true)]
    [InlineData("HTTP://UPPER.example.com/x.png", true)]
    [InlineData("http://89.105.207.42:32400/photo/:/transcode?width=300&url=/library/metadata/1", true)]
    // PHP-serialized fragment leaked by some Xtream panels ("s" parses as a URI scheme).
    [InlineData("s:308:/images/elNvW4oP3e68jZthz72vkJbsyPRj.jpeg", false)]
    [InlineData("/images/poster.jpeg", false)]
    [InlineData("image.tmdb.org/t/p/w600/abc.jpg", false)]
    [InlineData("file://server/share/poster.jpg", false)]
    [InlineData("ftp://example.com/poster.jpg", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHttp_AcceptsOnlyAbsoluteHttpUrls(string? value, bool expected) =>
        WebUrl.IsHttp(value).Should().Be(expected);

    [Fact]
    public void NullIfNotHttp_PassesGoodUrlsAndDropsJunk()
    {
        WebUrl.NullIfNotHttp("https://img.example.com/a.jpg").Should().Be("https://img.example.com/a.jpg");
        WebUrl.NullIfNotHttp("s:308:/images/abc.jpeg").Should().BeNull();
        WebUrl.NullIfNotHttp(null).Should().BeNull();
    }
}
