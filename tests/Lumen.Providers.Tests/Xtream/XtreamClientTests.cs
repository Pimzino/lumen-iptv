using System.Net;
using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xtream;

public sealed class XtreamClientTests
{
    private static readonly XtreamCredentials Credentials =
        new("http://portal.example.com:8080", "user", "pass");

    private static XtreamClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), Credentials, NullLogger<XtreamClient>.Instance);

    [Fact]
    public async Task Authenticate_ParsesNumbersAsStringsAndStringsAsNumbers()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(FixtureFile.ReadText("xtream-auth.json"));
        var client = CreateClient(handler);

        var auth = await client.AuthenticateAsync(CancellationToken.None);

        auth.IsAuthenticated.Should().BeTrue();
        auth.IsActive.Should().BeTrue();
        auth.UserInfo!.Username.Should().Be("demo");
        auth.UserInfo.MaxConnections.Should().Be(2);
        auth.UserInfo.ActiveConnections.Should().Be(1);
        auth.UserInfo.IsTrial.Should().BeFalse();
        auth.UserInfo.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790812800));
        auth.UserInfo.AllowedOutputFormats.Should().BeEquivalentTo("m3u8", "ts");
        auth.ServerInfo!.Port.Should().Be("8080");
        auth.ServerInfo.Timezone.Should().Be("Europe/London");
    }

    [Fact]
    public async Task Authenticate_SurfacesExpiredStatus()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(FixtureFile.ReadText("xtream-auth-expired.json"));
        var client = CreateClient(handler);

        var auth = await client.AuthenticateAsync(CancellationToken.None);

        auth.IsAuthenticated.Should().BeTrue();
        auth.IsActive.Should().BeFalse();
        auth.UserInfo!.Status.Should().Be("Expired");
        auth.UserInfo.MaxConnections.Should().BeNull(); // empty string tolerated
    }

    [Fact]
    public async Task HtmlResponse_ThrowsFriendlyApiException()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.HtmlResponse(FixtureFile.ReadText("xtream-error.html")));
        var client = CreateClient(handler);

        var act = () => client.AuthenticateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>()
            .WithMessage("*web page instead of data*");
    }

    [Fact]
    public async Task HttpError_ThrowsApiExceptionWithStatus()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = CreateClient(handler);

        var act = () => client.GetLiveCategoriesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>().WithMessage("*403*");
    }

    [Fact]
    public async Task GetLiveStreams_SkipsMalformedItems_ParsesTheRest()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            FixtureFile.ReadText("xtream-live-streams-messy.json"));
        var client = CreateClient(handler);

        var streams = await client.GetLiveStreamsAsync(null, CancellationToken.None);

        // The bare string element is skipped; the object with junk values parses with nulls.
        streams.Should().HaveCount(4);
        streams[0].StreamId.Should().Be("101");
        streams[0].EpgChannelId.Should().Be("bbc1.uk");
        streams[0].HasArchive.Should().BeTrue();
        streams[1].StreamId.Should().Be("102");
        streams[1].Number.Should().Be(2);
        streams[1].CategoryId.Should().Be("7");
        streams[2].StreamId.Should().BeNull();
        streams[2].Name.Should().BeNull();
        streams[3].Name.Should().Be("Channel 4");
    }

    [Fact]
    public async Task GetCategories_CoercesMixedIdTypes()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(FixtureFile.ReadText("xtream-categories.json"));
        var client = CreateClient(handler);

        var categories = await client.GetLiveCategoriesAsync(CancellationToken.None);

        categories.Should().HaveCount(2);
        categories[0].CategoryId.Should().Be("5");
        categories[1].CategoryId.Should().Be("7");
        categories[1].CategoryName.Should().Be("Sports");
    }

    [Fact]
    public async Task EmptyObjectResponse_YieldsEmptyList()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("{}");
        var client = CreateClient(handler);

        var categories = await client.GetVodCategoriesAsync(CancellationToken.None);

        categories.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVodInfo_ParsesNestedDetails()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(FixtureFile.ReadText("xtream-vod-info.json"));
        var client = CreateClient(handler);

        var info = await client.GetVodInfoAsync("7", CancellationToken.None);

        info.Should().NotBeNull();
        info!.Info!.DurationSeconds.Should().Be(8130);
        info.Info.Rating.Should().Be(7.5); // "7,5" localized decimal
        info.Info.BackdropPath.Should().ContainSingle().Which.Should().Be("http://img/bd.jpg");
        info.MovieData!.StreamId.Should().Be("7");
        info.MovieData.ContainerExtension.Should().Be("mkv");
    }

    [Fact]
    public async Task GetSeriesInfo_ParsesEpisodesAsObject()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            FixtureFile.ReadText("xtream-series-info-object.json"));
        var client = CreateClient(handler);

        var info = await client.GetSeriesInfoAsync("55", CancellationToken.None);

        info!.Episodes.Should().ContainKeys("1", "2");
        info.Episodes!["1"].Should().HaveCount(2);
        info.Episodes["1"][0].Id.Should().Be("9001");
        info.Episodes["1"][0].EpisodeNumber.Should().Be(1);
        info.Episodes["1"][0].Info!.DurationSeconds.Should().Be(2700);
        info.Episodes["1"][1].Season.Should().Be(1); // "1" as string
        info.Episodes["2"][0].Info.Should().BeNull();
        info.Info!.Rating.Should().Be(8.3);
    }

    [Fact]
    public async Task GetSeriesInfo_ParsesEpisodesAsArrayOfArrays()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            FixtureFile.ReadText("xtream-series-info-array.json"));
        var client = CreateClient(handler);

        var info = await client.GetSeriesInfoAsync("56", CancellationToken.None);

        info!.Episodes.Should().ContainKeys("1", "2");
        info.Episodes!["2"].Should().ContainSingle(); // the broken entry is skipped
        info.Episodes["2"][0].Id.Should().Be("2");
    }

    [Fact]
    public async Task GetShortEpg_DecodesBase64Titles()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(FixtureFile.ReadText("xtream-short-epg.json"));
        var client = CreateClient(handler);

        var listings = await client.GetShortEpgAsync("101", 4, CancellationToken.None);

        listings.Should().HaveCount(2);
        listings[0].DecodedTitle.Should().Be("Evening News");
        listings[0].DecodedDescription.Should().Be("The day's events.");
        listings[0].StartUnix.Should().Be(1783195200);
        listings[1].DecodedTitle.Should().Be("Plain");
    }

    [Fact]
    public async Task Requests_CarryCredentialsAndAction()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("[]");
        var client = CreateClient(handler);

        await client.GetLiveStreamsAsync("9", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var uri = handler.Requests[0]!.AbsoluteUri;
        uri.Should().StartWith("http://portal.example.com:8080/player_api.php?username=user&password=pass");
        uri.Should().Contain("action=get_live_streams");
        uri.Should().Contain("category_id=9");
    }
}
