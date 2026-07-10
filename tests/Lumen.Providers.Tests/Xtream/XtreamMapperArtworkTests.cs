using FluentAssertions;
using Lumen.Providers.Xtream;

namespace Lumen.Providers.Tests.Xtream;

/// <summary>
/// Panels sometimes leak PHP-serialized fragments or relative paths into artwork fields;
/// those must land as null (so artwork enrichment fills the gap), never as a "poster".
/// </summary>
public sealed class XtreamMapperArtworkTests
{
    private static readonly Dictionary<string, long> Categories = new(StringComparer.Ordinal) { ["9"] = 42 };

    [Fact]
    public void ToSeries_KeepsRealCoverUrl()
    {
        var item = XtreamMapper.ToSeries(
            new XtreamSeries
            {
                SeriesId = "11",
                Name = "1899",
                Cover = "http://img.example.com/1899.jpg",
                CategoryId = "9",
            },
            profileId: 1, Categories);

        item!.PosterUrl.Should().Be("http://img.example.com/1899.jpg");
        item.CategoryId.Should().Be(42);
    }

    [Theory]
    [InlineData("s:308:/images/elNvW4oP3e68jZthz72vkJbsyPRj.jpeg")] // PHP-serialized leak
    [InlineData("/images/poster.jpeg")] // relative path
    [InlineData("   ")]
    [InlineData(null)]
    public void ToSeries_DropsUnusableCoverValues(string? cover)
    {
        var item = XtreamMapper.ToSeries(
            new XtreamSeries { SeriesId = "11", Name = "1899", Cover = cover, CategoryId = "9" },
            profileId: 1, Categories);

        item!.PosterUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("s:120:/images/abc.jpeg")]
    [InlineData("cover.png")]
    public void ToMovie_DropsUnusableStreamIcon(string icon)
    {
        var item = XtreamMapper.ToMovie(
            new XtreamVodStream { StreamId = "7", Name = "Movie", StreamIcon = icon, CategoryId = "9" },
            profileId: 1, Categories);

        item!.PosterUrl.Should().BeNull();
    }

    [Fact]
    public void ToChannel_DropsUnusableLogo()
    {
        var channel = XtreamMapper.ToChannel(
            new XtreamLiveStream { StreamId = "5", Name = "Channel", StreamIcon = "s:80:/logo.png" },
            profileId: 1, Categories, nowUnix: 0);

        channel!.LogoUrl.Should().BeNull();
    }

    [Fact]
    public void ToSeriesDetails_SkipsNonUrlBackdrops()
    {
        var details = XtreamMapper.ToSeriesDetails(new XtreamSeriesInfo
        {
            Info = new XtreamSeriesInfoDetails
            {
                BackdropPath = ["s:99:/backdrop.jpg", "https://img.example.com/backdrop.jpg"],
            },
        });

        details.BackdropUrl.Should().Be("https://img.example.com/backdrop.jpg");
    }
}
