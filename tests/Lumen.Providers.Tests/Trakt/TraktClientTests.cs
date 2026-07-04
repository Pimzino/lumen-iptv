using System.Net;
using FluentAssertions;
using Lumen.Providers.Tests.Support;
using Lumen.Providers.Trakt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Trakt;

public sealed class TraktClientTests
{
    private static readonly TraktAppCredentials App = new("client-id", "client-secret");
    private static readonly TraktAccess Access = new("client-id", "access-token");

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static TraktClient CreateClient(StubHttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), NullLogger<TraktClient>.Instance);

    [Fact]
    public async Task StartDeviceAuth_ParsesCodes()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            { "device_code": "dev123", "user_code": "A1B2C3", "verification_url": "https://trakt.tv/activate",
              "expires_in": 600, "interval": 5 }
            """);
        var client = CreateClient(handler);

        var code = await client.StartDeviceAuthAsync(App, CancellationToken.None);

        code.UserCode.Should().Be("A1B2C3");
        code.DeviceCode.Should().Be("dev123");
        code.Interval.Should().Be(5);
        handler.Requests.Single()!.AbsolutePath.Should().Be("/oauth/device/code");
    }

    [Theory]
    [InlineData(400, TraktDeviceTokenStatus.Pending)]
    [InlineData(404, TraktDeviceTokenStatus.Invalid)]
    [InlineData(409, TraktDeviceTokenStatus.AlreadyUsed)]
    [InlineData(410, TraktDeviceTokenStatus.Expired)]
    [InlineData(418, TraktDeviceTokenStatus.Denied)]
    [InlineData(429, TraktDeviceTokenStatus.SlowDown)]
    public async Task PollDeviceToken_MapsEveryPollStatus(int httpStatus, TraktDeviceTokenStatus expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)httpStatus));
        var client = CreateClient(handler);

        var result = await client.PollDeviceTokenAsync(App, "dev123", CancellationToken.None);

        result.Status.Should().Be(expected);
        result.Tokens.Should().BeNull();
    }

    [Fact]
    public async Task PollDeviceToken_ReturnsTokensOnApproval()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            { "access_token": "acc", "refresh_token": "ref", "expires_in": 604800,
              "created_at": 1700000000, "token_type": "bearer", "scope": "public" }
            """);
        var client = CreateClient(handler);

        var result = await client.PollDeviceTokenAsync(App, "dev123", CancellationToken.None);

        result.Status.Should().Be(TraktDeviceTokenStatus.Authorized);
        result.Tokens!.AccessToken.Should().Be("acc");
        result.Tokens.RefreshToken.Should().Be("ref");
        result.Tokens.ExpiresIn.Should().Be(604800);
    }

    [Fact]
    public async Task Scrobble_Movie_SendsIdsAndProgress()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return StubHttpMessageHandler.JsonResponse(
                """{ "id": 1, "action": "scrobble", "progress": 97.5 }""", HttpStatusCode.Created);
        });
        var client = CreateClient(handler);

        var outcome = await client.ScrobbleAsync(
            Access, TraktScrobbleAction.Stop,
            new TraktScrobbleItem(new TraktIds { Tmdb = 603 }, null, null, null), 97.5, CancellationToken.None);

        outcome.Should().Be(TraktScrobbleOutcome.Recorded);
        handler.Requests.Single()!.AbsolutePath.Should().Be("/scrobble/stop");
        body.Should().Contain("\"tmdb\":603").And.Contain("\"progress\":97.5");
        body.Should().NotContain("\"show\"", "a movie scrobble has no show payload");
    }

    [Fact]
    public async Task Scrobble_Episode_SendsShowIdsPlusSeasonAndNumber()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return StubHttpMessageHandler.JsonResponse(
                """{ "id": 2, "action": "start", "progress": 3 }""", HttpStatusCode.Created);
        });
        var client = CreateClient(handler);

        var outcome = await client.ScrobbleAsync(
            Access, TraktScrobbleAction.Start,
            new TraktScrobbleItem(null, new TraktIds { Trakt = 50 }, Season: 1, EpisodeNumber: 2), 3,
            CancellationToken.None);

        outcome.Should().Be(TraktScrobbleOutcome.Recorded);
        handler.Requests.Single()!.AbsolutePath.Should().Be("/scrobble/start");
        body.Should().Contain("\"show\"").And.Contain("\"trakt\":50");
        body.Should().Contain("\"season\":1").And.Contain("\"number\":2");
    }

    [Theory]
    [InlineData(409, TraktScrobbleOutcome.Duplicate)]
    [InlineData(401, TraktScrobbleOutcome.Unauthorized)]
    [InlineData(500, TraktScrobbleOutcome.Failed)]
    public async Task Scrobble_SoftensFailuresIntoOutcomes(int httpStatus, TraktScrobbleOutcome expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)httpStatus));
        var client = CreateClient(handler);

        var outcome = await client.ScrobbleAsync(
            Access, TraktScrobbleAction.Stop,
            new TraktScrobbleItem(new TraktIds { Tmdb = 603 }, null, null, null), 90, CancellationToken.None);

        outcome.Should().Be(expected);
    }

    [Fact]
    public async Task GetWatchedShows_ParsesNestedSeasonsAndIds()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            [
              {
                "plays": 8, "last_watched_at": "2026-06-01T21:00:00.000Z",
                "show": { "title": "The Bear", "year": 2022,
                          "ids": { "trakt": 50, "slug": "the-bear", "tmdb": 136315, "imdb": "tt14452776" } },
                "seasons": [
                  { "number": 1, "episodes": [
                      { "number": 1, "plays": 1, "last_watched_at": "2026-05-30T20:00:00.000Z" },
                      { "number": 2, "plays": 2, "last_watched_at": "2026-06-01T21:00:00.000Z" } ] }
                ]
              }
            ]
            """);
        var client = CreateClient(handler);

        var shows = await client.GetWatchedShowsAsync(Access, CancellationToken.None);

        var show = shows.Single();
        show.Show!.Ids!.Trakt.Should().Be(50);
        show.Show.Ids.Tmdb.Should().Be(136315);
        var episodes = show.Seasons!.Single().Episodes!;
        episodes.Should().HaveCount(2);
        episodes[1].Plays.Should().Be(2);
        episodes[1].LastWatchedAt.Should().Be(DateTimeOffset.Parse("2026-06-01T21:00:00Z", null));
    }

    [Fact]
    public async Task GetWatchedMovies_ParsesIdsAndPlays()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """
            [ { "plays": 3, "last_watched_at": "2026-01-02T03:04:05.000Z",
                "movie": { "title": "The Matrix", "year": 1999,
                           "ids": { "trakt": 1, "slug": "the-matrix-1999", "imdb": "tt0133093", "tmdb": 603 } } } ]
            """);
        var client = CreateClient(handler);

        var movies = await client.GetWatchedMoviesAsync(Access, CancellationToken.None);

        movies.Single().Plays.Should().Be(3);
        movies.Single().Movie!.Ids!.Tmdb.Should().Be(603);
    }

    [Fact]
    public async Task RequiredHeaders_AreOnEveryCall()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            seen = request;
            return StubHttpMessageHandler.JsonResponse("[]");
        });
        var client = CreateClient(handler);

        await client.GetWatchedMoviesAsync(Access, CancellationToken.None);

        seen!.Headers.GetValues("trakt-api-key").Single().Should().Be("client-id");
        seen.Headers.GetValues("trakt-api-version").Single().Should().Be("2");
        seen.Headers.Authorization!.Parameter.Should().Be("access-token");
    }

    [Fact]
    public async Task ApiError_ThrowsFriendlyException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        var act = () => client.GetWatchedMoviesAsync(Access, CancellationToken.None);

        await act.Should().ThrowAsync<TraktApiException>().WithMessage("*reconnect in Settings*");
    }
}
