namespace Lumen.Providers.Trakt;

/// <summary>Client id/secret of the user's registered Trakt API app (trakt.tv/oauth/applications).</summary>
public sealed record TraktAppCredentials(string ClientId, string ClientSecret);

/// <summary>Per-call authorization: the API app's client id plus the user's access token.</summary>
public sealed record TraktAccess(string ClientId, string AccessToken);

/// <summary>Outcome of one /oauth/device/token poll.</summary>
public enum TraktDeviceTokenStatus
{
    /// <summary>Tokens issued; stop polling.</summary>
    Authorized,

    /// <summary>User hasn't entered the code yet; keep polling.</summary>
    Pending,

    /// <summary>Polling too fast; back off.</summary>
    SlowDown,

    /// <summary>Codes expired; restart the flow.</summary>
    Expired,

    /// <summary>User denied the app.</summary>
    Denied,

    /// <summary>Code already redeemed; restart the flow.</summary>
    AlreadyUsed,

    /// <summary>Unknown device code; restart the flow.</summary>
    Invalid,
}

public sealed record TraktDeviceTokenResult(TraktDeviceTokenStatus Status, TraktTokenResponse? Tokens);

/// <summary>Outcome of a scrobble call (failures are soft — playback must never care).</summary>
public enum TraktScrobbleOutcome
{
    Recorded,

    /// <summary>409: the same item was scrobbled moments ago.</summary>
    Duplicate,

    /// <summary>401/403: token expired or revoked.</summary>
    Unauthorized,

    Failed,
}

/// <summary>What to scrobble: a movie's ids, or a show's ids plus season/episode numbers.</summary>
public sealed record TraktScrobbleItem(TraktIds? MovieIds, TraktIds? ShowIds, int? Season, int? EpisodeNumber);

public enum TraktScrobbleAction
{
    Start,
    Pause,
    Stop,
}

/// <summary>A Trakt API failure with a user-explainable message.</summary>
public sealed class TraktApiException : Exception
{
    public TraktApiException(string message)
        : base(message)
    {
    }

    public TraktApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TraktApiException()
    {
    }
}

/// <summary>Typed surface over the Trakt API v2 (device auth, scrobbling, watched sync, search).</summary>
public interface ITraktClient
{
    /// <summary>Starts device auth: returns the code the user enters at trakt.tv/activate.</summary>
    Task<TraktDeviceCodeResponse> StartDeviceAuthAsync(TraktAppCredentials app, CancellationToken cancellationToken);

    /// <summary>One token poll; the caller loops on <see cref="TraktDeviceTokenStatus.Pending"/> at the advertised interval.</summary>
    Task<TraktDeviceTokenResult> PollDeviceTokenAsync(
        TraktAppCredentials app, string deviceCode, CancellationToken cancellationToken);

    /// <summary>Exchanges a refresh token for fresh tokens; null when the grant was rejected (re-auth needed).</summary>
    Task<TraktTokenResponse?> RefreshTokenAsync(
        TraktAppCredentials app, string refreshToken, CancellationToken cancellationToken);

    /// <summary>The connected account's username (GET /users/settings).</summary>
    Task<string?> GetUsernameAsync(TraktAccess access, CancellationToken cancellationToken);

    Task<TraktScrobbleOutcome> ScrobbleAsync(
        TraktAccess access, TraktScrobbleAction action, TraktScrobbleItem item, double progressPercent,
        CancellationToken cancellationToken);

    Task<TraktLastActivities?> GetLastActivitiesAsync(TraktAccess access, CancellationToken cancellationToken);

    Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(TraktAccess access, CancellationToken cancellationToken);

    Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(TraktAccess access, CancellationToken cancellationToken);

    Task AddToHistoryAsync(TraktAccess access, TraktHistoryRequest items, CancellationToken cancellationToken);

    Task RemoveFromHistoryAsync(TraktAccess access, TraktHistoryRequest items, CancellationToken cancellationToken);

    /// <summary>Text search; <paramref name="type"/> is "movie" or "show".</summary>
    Task<IReadOnlyList<TraktSearchResult>> SearchAsync(
        TraktAccess access, string type, string query, int? year, CancellationToken cancellationToken);
}
