using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Xtream;

/// <summary>TMDB/IMDB ids in detail responses (they drive exact Trakt matching).</summary>
public sealed class XtreamExternalIdsTests
{
    private static XtreamClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new XtreamCredentials("http://portal.example.com:8080", "u", "p"),
            NullLogger<XtreamClient>.Instance);

    [Fact]
    public async Task VodInfo_ParsesTmdbIdAsStringAndMapsIt()
    {
        // Panels send tmdb_id as a string as often as a number; both must land.
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            { "info": { "plot": "A hacker learns the truth.", "tmdb_id": "603", "imdb_id": "tt0133093" },
              "movie_data": { "stream_id": 42, "container_extension": "mkv" } }
            """);
        var client = CreateClient(handler);

        var info = await client.GetVodInfoAsync("42", CancellationToken.None);
        var details = XtreamMapper.ToMovieDetails(info!);

        details.TmdbId.Should().Be(603);
        details.ImdbId.Should().Be("tt0133093");
    }

    [Fact]
    public async Task VodInfo_FallsBackToTheTmdbFieldName()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{ "info": { "tmdb": 27205 }, "movie_data": { "stream_id": 7 } }""");
        var client = CreateClient(handler);

        var info = await client.GetVodInfoAsync("7", CancellationToken.None);
        var details = XtreamMapper.ToMovieDetails(info!);

        details.TmdbId.Should().Be(27205);
    }

    [Fact]
    public async Task SeriesInfo_ParsesShowTmdbId_AndGarbageYieldsNull()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            { "info": { "name": "The Bear", "tmdb_id": 136315, "imdb_id": "" },
              "episodes": { "1": [ { "id": "900", "episode_num": 1, "season": 1, "title": "System" } ] } }
            """);
        var client = CreateClient(handler);

        var info = await client.GetSeriesInfoAsync("55", CancellationToken.None);
        var details = XtreamMapper.ToSeriesDetails(info!);

        details.TmdbId.Should().Be(136315);
        details.ImdbId.Should().BeNull("blank strings mean absent");
        details.Seasons.Single().Episodes.Single().ProviderEpisodeId.Should().Be("900");
    }
}
