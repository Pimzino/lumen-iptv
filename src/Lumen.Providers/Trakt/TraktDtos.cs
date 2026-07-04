using System.Text.Json.Serialization;

namespace Lumen.Providers.Trakt;

// Response and request payloads for the Trakt API v2 (https://trakt.docs.apiary.io/).
// Property names follow Trakt's snake_case JSON via explicit attributes, matching the
// Xtream DTO idiom.

public sealed class TraktDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_url")]
    public string? VerificationUrl { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public sealed class TraktTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}

public sealed class TraktIds
{
    [JsonPropertyName("trakt")]
    public long? Trakt { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }

    [JsonPropertyName("tmdb")]
    public long? Tmdb { get; set; }
}

public sealed class TraktMovie
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}

public sealed class TraktShow
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}

public sealed class TraktWatchedMovie
{
    [JsonPropertyName("plays")]
    public int Plays { get; set; }

    [JsonPropertyName("last_watched_at")]
    public DateTimeOffset? LastWatchedAt { get; set; }

    [JsonPropertyName("movie")]
    public TraktMovie? Movie { get; set; }
}

public sealed class TraktWatchedShow
{
    [JsonPropertyName("plays")]
    public int Plays { get; set; }

    [JsonPropertyName("last_watched_at")]
    public DateTimeOffset? LastWatchedAt { get; set; }

    [JsonPropertyName("show")]
    public TraktShow? Show { get; set; }

    [JsonPropertyName("seasons")]
    public List<TraktWatchedSeason>? Seasons { get; set; }
}

public sealed class TraktWatchedSeason
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("episodes")]
    public List<TraktWatchedEpisode>? Episodes { get; set; }
}

public sealed class TraktWatchedEpisode
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("plays")]
    public int Plays { get; set; }

    [JsonPropertyName("last_watched_at")]
    public DateTimeOffset? LastWatchedAt { get; set; }
}

/// <summary>Subset of /sync/last_activities used to gate pulls (raw timestamps, compared ordinally).</summary>
public sealed class TraktLastActivities
{
    [JsonPropertyName("movies")]
    public TraktActivityTimes? Movies { get; set; }

    [JsonPropertyName("episodes")]
    public TraktActivityTimes? Episodes { get; set; }
}

public sealed class TraktActivityTimes
{
    [JsonPropertyName("watched_at")]
    public string? WatchedAt { get; set; }
}

public sealed class TraktUserSettings
{
    [JsonPropertyName("user")]
    public TraktUser? User { get; set; }
}

public sealed class TraktUser
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public sealed class TraktSearchResult
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("movie")]
    public TraktMovie? Movie { get; set; }

    [JsonPropertyName("show")]
    public TraktShow? Show { get; set; }
}

public sealed class TraktScrobbleResponse
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }
}

// ------------------------------------------------------------------ request bodies

public sealed class TraktDeviceCodeRequest
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;
}

public sealed class TraktDeviceTokenRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class TraktRefreshTokenRequest
{
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("redirect_uri")]
    public string RedirectUri { get; set; } = "urn:ietf:wg:oauth:2.0:oob";

    [JsonPropertyName("grant_type")]
    public string GrantType { get; set; } = "refresh_token";
}

public sealed class TraktScrobbleRequest
{
    [JsonPropertyName("movie")]
    public TraktMovie? Movie { get; set; }

    [JsonPropertyName("show")]
    public TraktShow? Show { get; set; }

    [JsonPropertyName("episode")]
    public TraktEpisodeNumberRef? Episode { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }
}

/// <summary>Episode addressed by season/number under an accompanying show.</summary>
public sealed class TraktEpisodeNumberRef
{
    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }
}

public sealed class TraktHistoryRequest
{
    [JsonPropertyName("movies")]
    public List<TraktHistoryMovie>? Movies { get; set; }

    [JsonPropertyName("shows")]
    public List<TraktHistoryShow>? Shows { get; set; }
}

public sealed class TraktHistoryMovie
{
    [JsonPropertyName("watched_at")]
    public DateTimeOffset? WatchedAt { get; set; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }
}

public sealed class TraktHistoryShow
{
    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; set; }

    [JsonPropertyName("seasons")]
    public List<TraktHistorySeason>? Seasons { get; set; }
}

public sealed class TraktHistorySeason
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("episodes")]
    public List<TraktHistoryEpisode>? Episodes { get; set; }
}

public sealed class TraktHistoryEpisode
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("watched_at")]
    public DateTimeOffset? WatchedAt { get; set; }
}
