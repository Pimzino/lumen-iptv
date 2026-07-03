using System.Text.Json;
using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Xtream;
using Lumen.Providers.Xtream.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xtream;

public sealed class XtreamClientEdgeCaseTests
{
    private static readonly XtreamCredentials Credentials =
        new("http://portal.example.com:8080", "user", "pass");

    private static XtreamClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), Credentials, NullLogger<XtreamClient>.Instance);

    [Fact]
    public async Task VodAndSeriesEndpoints_ParseArrays()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("get_vod_streams", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.JsonResponse(
                    """[{"stream_id":7,"name":"The Long Voyage","rating":"7.2","added":"1710000000","category_id":"12","container_extension":"mkv"}]""");
            }

            if (uri.Contains("get_series_categories", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.JsonResponse(
                    """[{"category_id":"3","category_name":"Drama"}]""");
            }

            if (uri.Contains("get_series", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.JsonResponse(
                    """[{"series_id":55,"name":"Breaking Code","cover":"http://img/c.jpg","rating":8.3,"backdrop_path":["http://img/b.jpg"],"last_modified":"1700000123","category_id":3}]""");
            }

            if (uri.Contains("get_simple_data_table", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.JsonResponse(
                    """{"epg_listings":[{"id":"1","title":"UGxhaW4=","start_timestamp":"100","stop_timestamp":"200"}]}""");
            }

            return StubHttpMessageHandler.JsonResponse("[]");
        });

        var client = CreateClient(handler);

        var vod = await client.GetVodStreamsAsync("12", CancellationToken.None);
        vod.Should().ContainSingle();
        vod[0].StreamId.Should().Be("7");
        vod[0].Rating.Should().Be(7.2);
        vod[0].ContainerExtension.Should().Be("mkv");

        var seriesCategories = await client.GetSeriesCategoriesAsync(CancellationToken.None);
        seriesCategories.Should().ContainSingle().Which.CategoryName.Should().Be("Drama");

        var series = await client.GetSeriesAsync(null, CancellationToken.None);
        series.Should().ContainSingle();
        series[0].SeriesId.Should().Be("55");
        series[0].LastModifiedUnix.Should().Be(1700000123);
        series[0].BackdropPath.Should().ContainSingle();

        var table = await client.GetSimpleDataTableAsync("101", CancellationToken.None);
        table.Should().ContainSingle().Which.DecodedTitle.Should().Be("Plain");
    }

    [Fact]
    public async Task ConnectionFailure_WrapsInApiException()
    {
        var handler = new ThrowingHandler(() => new HttpRequestException("connection refused"));
        var client = new XtreamClient(new HttpClient(handler), Credentials, NullLogger<XtreamClient>.Instance);

        var act = () => client.AuthenticateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>().WithMessage("*Could not reach*");
    }

    [Fact]
    public async Task Timeout_WrapsInApiException()
    {
        var handler = new ThrowingHandler(() => new TaskCanceledException("timed out"));
        var client = new XtreamClient(new HttpClient(handler), Credentials, NullLogger<XtreamClient>.Instance);

        var act = () => client.GetLiveCategoriesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>().WithMessage("*did not respond*");
    }

    [Fact]
    public async Task NonJsonNonHtmlGarbage_ThrowsApiException()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("this is not json at all");
        var client = CreateClient(handler);

        var act = () => client.AuthenticateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public async Task NullAuthResponse_ThrowsApiException()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("null");
        var client = CreateClient(handler);

        var act = () => client.AuthenticateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<XtreamApiException>().WithMessage("*empty*");
    }

    [Fact]
    public async Task MalformedInfoResponses_ReturnNullInsteadOfThrowing()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("[1,2,3]");
        var client = CreateClient(handler);

        (await client.GetVodInfoAsync("7", CancellationToken.None)).Should().BeNull();
        (await client.GetSeriesInfoAsync("7", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void SerializingSeriesInfo_IsUnsupported()
    {
        var info = new XtreamSeriesInfo { Episodes = new Dictionary<string, List<XtreamEpisode>>() };

        var act = () => JsonSerializer.Serialize(info, XtreamJsonContext.Default.XtreamSeriesInfo);

        act.Should().Throw<NotSupportedException>();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _exceptionFactory;

        public ThrowingHandler(Func<Exception> exceptionFactory)
        {
            _exceptionFactory = exceptionFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exceptionFactory());
    }
}
