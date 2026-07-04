using System.Text.Json.Serialization;

namespace Lumen.Providers.Trakt;

/// <summary>Source-generated serializer metadata for all Trakt payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TraktDeviceCodeRequest))]
[JsonSerializable(typeof(TraktDeviceCodeResponse))]
[JsonSerializable(typeof(TraktDeviceTokenRequest))]
[JsonSerializable(typeof(TraktRefreshTokenRequest))]
[JsonSerializable(typeof(TraktTokenResponse))]
[JsonSerializable(typeof(TraktUserSettings))]
[JsonSerializable(typeof(TraktLastActivities))]
[JsonSerializable(typeof(List<TraktWatchedMovie>))]
[JsonSerializable(typeof(List<TraktWatchedShow>))]
[JsonSerializable(typeof(List<TraktSearchResult>))]
[JsonSerializable(typeof(TraktScrobbleRequest))]
[JsonSerializable(typeof(TraktScrobbleResponse))]
[JsonSerializable(typeof(TraktHistoryRequest))]
internal sealed partial class TraktJsonContext : JsonSerializerContext
{
}
