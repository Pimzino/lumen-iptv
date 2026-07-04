using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Lumen.Providers.Trakt;

/// <summary>Default <see cref="ITraktClient"/> over the "trakt" named HttpClient.</summary>
public sealed class TraktClient : ITraktClient
{
    /// <summary>Named HttpClient used for Trakt API calls.</summary>
    public const string HttpClientName = "trakt";

    private static readonly Uri BaseUri = new("https://api.trakt.tv/");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TraktClient> _logger;

    public TraktClient(IHttpClientFactory httpClientFactory, ILogger<TraktClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TraktDeviceCodeResponse> StartDeviceAuthAsync(
        TraktAppCredentials app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        using var response = await SendAsync(
            HttpMethod.Post, "oauth/device/code", app.ClientId, accessToken: null,
            JsonContent.Create(new TraktDeviceCodeRequest { ClientId = app.ClientId }, TraktJsonContext.Default.TraktDeviceCodeRequest),
            cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "starting device sign-in").ConfigureAwait(false);
        var code = await ReadAsAsync(response, TraktJsonContext.Default.TraktDeviceCodeResponse, cancellationToken)
            .ConfigureAwait(false);
        if (code?.DeviceCode is null || code.UserCode is null)
        {
            throw new TraktApiException("Trakt returned an empty device-code response.");
        }

        return code;
    }

    public async Task<TraktDeviceTokenResult> PollDeviceTokenAsync(
        TraktAppCredentials app, string deviceCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        using var response = await SendAsync(
            HttpMethod.Post, "oauth/device/token", app.ClientId, accessToken: null,
            JsonContent.Create(
                new TraktDeviceTokenRequest { Code = deviceCode, ClientId = app.ClientId, ClientSecret = app.ClientSecret },
                TraktJsonContext.Default.TraktDeviceTokenRequest),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var tokens = await ReadAsAsync(response, TraktJsonContext.Default.TraktTokenResponse, cancellationToken)
                .ConfigureAwait(false);
            return tokens?.AccessToken is null
                ? new TraktDeviceTokenResult(TraktDeviceTokenStatus.Invalid, null)
                : new TraktDeviceTokenResult(TraktDeviceTokenStatus.Authorized, tokens);
        }

        var status = (int)response.StatusCode switch
        {
            400 => TraktDeviceTokenStatus.Pending,
            404 => TraktDeviceTokenStatus.Invalid,
            409 => TraktDeviceTokenStatus.AlreadyUsed,
            410 => TraktDeviceTokenStatus.Expired,
            418 => TraktDeviceTokenStatus.Denied,
            429 => TraktDeviceTokenStatus.SlowDown,
            _ => TraktDeviceTokenStatus.Invalid,
        };
        return new TraktDeviceTokenResult(status, null);
    }

    public async Task<TraktTokenResponse?> RefreshTokenAsync(
        TraktAppCredentials app, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var response = await SendAsync(
            HttpMethod.Post, "oauth/token", app.ClientId, accessToken: null,
            JsonContent.Create(
                new TraktRefreshTokenRequest
                {
                    RefreshToken = refreshToken,
                    ClientId = app.ClientId,
                    ClientSecret = app.ClientSecret,
                },
                TraktJsonContext.Default.TraktRefreshTokenRequest),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("Trakt refresh token was rejected ({Status}); the user must reconnect", (int)response.StatusCode);
            return null;
        }

        await ThrowIfNotSuccessAsync(response, "refreshing the Trakt session").ConfigureAwait(false);
        return await ReadAsAsync(response, TraktJsonContext.Default.TraktTokenResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetUsernameAsync(TraktAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        using var response = await SendAsync(
            HttpMethod.Get, "users/settings", access.ClientId, access.AccessToken, content: null, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "reading the Trakt account").ConfigureAwait(false);
        var settings = await ReadAsAsync(response, TraktJsonContext.Default.TraktUserSettings, cancellationToken)
            .ConfigureAwait(false);
        return settings?.User?.Username;
    }

    public async Task<TraktScrobbleOutcome> ScrobbleAsync(
        TraktAccess access, TraktScrobbleAction action, TraktScrobbleItem item, double progressPercent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(item);

        var path = action switch
        {
            TraktScrobbleAction.Start => "scrobble/start",
            TraktScrobbleAction.Pause => "scrobble/pause",
            _ => "scrobble/stop",
        };
        var body = new TraktScrobbleRequest
        {
            Progress = Math.Clamp(progressPercent, 0, 100),
            Movie = item.MovieIds is null ? null : new TraktMovie { Ids = item.MovieIds },
            Show = item.ShowIds is null ? null : new TraktShow { Ids = item.ShowIds },
            Episode = item.ShowIds is null || item.Season is not { } season || item.EpisodeNumber is not { } number
                ? null
                : new TraktEpisodeNumberRef { Season = season, Number = number },
        };
        if (body.Movie is null && body.Episode is null)
        {
            return TraktScrobbleOutcome.Failed;
        }

        try
        {
            using var response = await SendAsync(
                HttpMethod.Post, path, access.ClientId, access.AccessToken,
                JsonContent.Create(body, TraktJsonContext.Default.TraktScrobbleRequest), cancellationToken)
                .ConfigureAwait(false);
            return (int)response.StatusCode switch
            {
                >= 200 and < 300 => TraktScrobbleOutcome.Recorded,
                409 => TraktScrobbleOutcome.Duplicate,
                401 or 403 => TraktScrobbleOutcome.Unauthorized,
                _ => TraktScrobbleOutcome.Failed,
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Trakt scrobble {Action} failed", action);
            return TraktScrobbleOutcome.Failed;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TraktScrobbleOutcome.Failed;
        }
    }

    public async Task<TraktLastActivities?> GetLastActivitiesAsync(TraktAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        using var response = await SendAsync(
            HttpMethod.Get, "sync/last_activities", access.ClientId, access.AccessToken, content: null, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "checking Trakt activity").ConfigureAwait(false);
        return await ReadAsAsync(response, TraktJsonContext.Default.TraktLastActivities, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(
        TraktAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        using var response = await SendAsync(
            HttpMethod.Get, "sync/watched/movies", access.ClientId, access.AccessToken, content: null, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "downloading watched movies").ConfigureAwait(false);
        var items = await ReadAsAsync(response, TraktJsonContext.Default.ListTraktWatchedMovie, cancellationToken)
            .ConfigureAwait(false);
        return items ?? [];
    }

    public async Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(
        TraktAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        using var response = await SendAsync(
            HttpMethod.Get, "sync/watched/shows", access.ClientId, access.AccessToken, content: null, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "downloading watched shows").ConfigureAwait(false);
        var items = await ReadAsAsync(response, TraktJsonContext.Default.ListTraktWatchedShow, cancellationToken)
            .ConfigureAwait(false);
        return items ?? [];
    }

    public async Task AddToHistoryAsync(TraktAccess access, TraktHistoryRequest items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(items);
        using var response = await SendAsync(
            HttpMethod.Post, "sync/history", access.ClientId, access.AccessToken,
            JsonContent.Create(items, TraktJsonContext.Default.TraktHistoryRequest), cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "updating Trakt history").ConfigureAwait(false);
    }

    public async Task RemoveFromHistoryAsync(
        TraktAccess access, TraktHistoryRequest items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(items);
        using var response = await SendAsync(
            HttpMethod.Post, "sync/history/remove", access.ClientId, access.AccessToken,
            JsonContent.Create(items, TraktJsonContext.Default.TraktHistoryRequest), cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "removing Trakt history").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TraktSearchResult>> SearchAsync(
        TraktAccess access, string type, string query, int? year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var path = $"search/{type}?query={Uri.EscapeDataString(query)}";
        if (year is { } y)
        {
            path += $"&years={y}";
        }

        using var response = await SendAsync(
            HttpMethod.Get, path, access.ClientId, access.AccessToken, content: null, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, "searching Trakt").ConfigureAwait(false);
        var items = await ReadAsAsync(response, TraktJsonContext.Default.ListTraktSearchResult, cancellationToken)
            .ConfigureAwait(false);
        return items ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string clientId, string? accessToken, HttpContent? content,
        CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        request.Content = content;
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TraktApiException($"Could not reach Trakt: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TraktApiException("Trakt did not respond in time.", ex);
        }
    }

    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, string activity)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = (int)response.StatusCode switch
        {
            401 or 403 => "the Trakt session is no longer valid — reconnect in Settings",
            420 => "the Trakt account has reached a plan limit",
            429 => "Trakt is rate-limiting requests — try again in a few minutes",
            >= 500 => "Trakt is having server trouble",
            _ => $"Trakt rejected the request ({(int)response.StatusCode} {response.ReasonPhrase})",
        };
        // Consume the body so the connection can be reused before we throw.
        await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        throw new TraktApiException($"{char.ToUpperInvariant(activity[0])}{activity[1..]} failed: {detail}.");
    }

    private async Task<T?> ReadAsAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Trakt response could not be parsed");
            return null;
        }
    }
}
