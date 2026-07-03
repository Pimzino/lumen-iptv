using FluentAssertions;
using Lumen.Providers.Xtream;

namespace Lumen.Providers.Tests.Xtream;

public sealed class XtreamUrlsTests
{
    private const string Server = "http://portal.example.com:8080";

    [Theory]
    [InlineData("example.com:8080", "http://example.com:8080")]
    [InlineData("http://example.com:8080/", "http://example.com:8080")]
    [InlineData("https://portal.example.com", "https://portal.example.com")]
    [InlineData("  http://example.com/panel/  ", "http://example.com/panel")]
    public void NormalizeServerBase_NormalizesSchemeAndTrailingSlash(string input, string expected)
        => XtreamUrls.NormalizeServerBase(input).Should().Be(expected);

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url //")]
    public void NormalizeServerBase_RejectsNonHttpServers(string input)
        => FluentActions.Invoking(() => XtreamUrls.NormalizeServerBase(input))
            .Should().Throw<FormatException>();

    [Fact]
    public void NormalizeServerBase_DropsQueryAndFragment()
        => XtreamUrls.NormalizeServerBase("http://example.com/panel?foo=1#frag")
            .Should().Be("http://example.com/panel");

    [Fact]
    public void PlayerApi_EscapesCredentials()
    {
        var uri = XtreamUrls.PlayerApi(Server, "user name", "p&ss=word");

        uri.AbsoluteUri.Should().Be(
            "http://portal.example.com:8080/player_api.php?username=user%20name&password=p%26ss%3Dword");
    }

    [Fact]
    public void PlayerApi_AppendsAction()
    {
        var uri = XtreamUrls.PlayerApi(Server, "u", "p", "get_live_streams");

        uri.AbsoluteUri.Should().EndWith("&action=get_live_streams");
    }

    [Fact]
    public void Live_DefaultsToMpegTs()
        => XtreamUrls.Live(Server, "u", "p", "42").AbsoluteUri
            .Should().Be("http://portal.example.com:8080/live/u/p/42.ts");

    [Fact]
    public void Live_SupportsHlsContainer()
        => XtreamUrls.Live(Server, "u", "p", "42", LiveStreamContainer.Hls).AbsoluteUri
            .Should().Be("http://portal.example.com:8080/live/u/p/42.m3u8");

    [Fact]
    public void Movie_UsesContainerExtension()
        => XtreamUrls.Movie(Server, "u", "p", "7", "mkv").AbsoluteUri
            .Should().Be("http://portal.example.com:8080/movie/u/p/7.mkv");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    public void Movie_FallsBackToMp4ForMissingExtension(string? extension)
        => XtreamUrls.Movie(Server, "u", "p", "7", extension).AbsoluteUri
            .Should().EndWith("/movie/u/p/7.mp4");

    [Fact]
    public void SeriesEpisode_BuildsSeriesPath()
        => XtreamUrls.SeriesEpisode(Server, "u", "p", "1001", ".mp4").AbsoluteUri
            .Should().Be("http://portal.example.com:8080/series/u/p/1001.mp4");

    [Fact]
    public void Xmltv_BuildsEpgEndpoint()
        => XtreamUrls.Xmltv(Server, "u", "p").AbsoluteUri
            .Should().Be("http://portal.example.com:8080/xmltv.php?username=u&password=p");

    [Fact]
    public void Live_RejectsEmptyStreamId()
        => FluentActions.Invoking(() => XtreamUrls.Live(Server, "u", "p", " "))
            .Should().Throw<ArgumentException>();
}
