using System.Text;
using System.Text.Json.Serialization;
using Lumen.Providers.Xtream.Json;

namespace Lumen.Providers.Xtream;

/// <summary>player_api.php authentication response.</summary>
public sealed class XtreamAuthResponse
{
    [JsonPropertyName("user_info")]
    public XtreamUserInfo? UserInfo { get; set; }

    [JsonPropertyName("server_info")]
    public XtreamServerInfo? ServerInfo { get; set; }

    /// <summary>True when the panel accepted the credentials.</summary>
    [JsonIgnore]
    public bool IsAuthenticated => UserInfo?.Auth is 1 or null && UserInfo?.Username is not null;

    /// <summary>True when the account status is anything but Active (Expired, Banned, Disabled).</summary>
    [JsonIgnore]
    public bool IsActive => string.Equals(UserInfo?.Status, "Active", StringComparison.OrdinalIgnoreCase);
}

public sealed class XtreamUserInfo
{
    [JsonPropertyName("username")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Username { get; set; }

    [JsonPropertyName("auth")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Auth { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Status { get; set; }

    [JsonPropertyName("exp_date")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? ExpDateUnix { get; set; }

    [JsonPropertyName("is_trial")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? IsTrial { get; set; }

    [JsonPropertyName("active_cons")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ActiveConnections { get; set; }

    [JsonPropertyName("max_connections")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? MaxConnections { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? CreatedAtUnix { get; set; }

    [JsonPropertyName("allowed_output_formats")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public IReadOnlyList<string>? AllowedOutputFormats { get; set; }

    [JsonIgnore]
    public DateTimeOffset? ExpiresAt =>
        ExpDateUnix is > 0 ? DateTimeOffset.FromUnixTimeSeconds(ExpDateUnix.Value) : null;
}

public sealed class XtreamServerInfo
{
    [JsonPropertyName("url")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Url { get; set; }

    [JsonPropertyName("port")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Port { get; set; }

    [JsonPropertyName("https_port")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? HttpsPort { get; set; }

    [JsonPropertyName("server_protocol")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Protocol { get; set; }

    [JsonPropertyName("timezone")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Timezone { get; set; }

    [JsonPropertyName("timestamp_now")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? TimestampNow { get; set; }

    /// <summary>The panel's current local wall-clock ("yyyy-MM-dd HH:mm:ss").</summary>
    [JsonPropertyName("time_now")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? TimeNow { get; set; }
}

public sealed class XtreamCategory
{
    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryName { get; set; }

    [JsonPropertyName("parent_id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ParentId { get; set; }
}

public sealed class XtreamLiveStream
{
    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamId { get; set; }

    [JsonPropertyName("num")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Number { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("epg_channel_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? EpgChannelId { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("added")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? AddedUnix { get; set; }

    [JsonPropertyName("tv_archive")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? HasArchive { get; set; }

    /// <summary>How many days back the catch-up archive reaches.</summary>
    [JsonPropertyName("tv_archive_duration")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ArchiveDurationDays { get; set; }
}

public sealed class XtreamVodStream
{
    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamId { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("added")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? AddedUnix { get; set; }

    [JsonPropertyName("container_extension")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ContainerExtension { get; set; }
}

public sealed class XtreamSeries
{
    [JsonPropertyName("series_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SeriesId { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("cover")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cover { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("plot")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Plot { get; set; }

    [JsonPropertyName("genre")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Genre { get; set; }

    [JsonPropertyName("cast")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cast { get; set; }

    [JsonPropertyName("director")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Director { get; set; }

    [JsonPropertyName("releaseDate")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("last_modified")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? LastModifiedUnix { get; set; }

    [JsonPropertyName("backdrop_path")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public IReadOnlyList<string>? BackdropPath { get; set; }

    [JsonPropertyName("youtube_trailer")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? YoutubeTrailer { get; set; }
}

public sealed class XtreamVodInfo
{
    [JsonPropertyName("info")]
    public XtreamVodInfoDetails? Info { get; set; }

    [JsonPropertyName("movie_data")]
    public XtreamMovieData? MovieData { get; set; }
}

public sealed class XtreamVodInfoDetails
{
    [JsonPropertyName("movie_image")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? MovieImage { get; set; }

    [JsonPropertyName("plot")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Plot { get; set; }

    [JsonPropertyName("description")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Description { get; set; }

    [JsonPropertyName("cast")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cast { get; set; }

    [JsonPropertyName("actors")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Actors { get; set; }

    [JsonPropertyName("director")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Director { get; set; }

    [JsonPropertyName("genre")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Genre { get; set; }

    [JsonPropertyName("releasedate")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("duration_secs")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("backdrop_path")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public IReadOnlyList<string>? BackdropPath { get; set; }

    [JsonPropertyName("youtube_trailer")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? YoutubeTrailer { get; set; }
}

public sealed class XtreamMovieData
{
    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamId { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("container_extension")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ContainerExtension { get; set; }

    [JsonPropertyName("added")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? AddedUnix { get; set; }
}

public sealed class XtreamSeriesInfo
{
    [JsonPropertyName("info")]
    public XtreamSeriesInfoDetails? Info { get; set; }

    [JsonPropertyName("episodes")]
    [JsonConverter(typeof(XtreamEpisodesConverter))]
    public Dictionary<string, List<XtreamEpisode>>? Episodes { get; set; }
}

public sealed class XtreamSeriesInfoDetails
{
    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("cover")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cover { get; set; }

    [JsonPropertyName("plot")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Plot { get; set; }

    [JsonPropertyName("cast")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cast { get; set; }

    [JsonPropertyName("director")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Director { get; set; }

    [JsonPropertyName("genre")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Genre { get; set; }

    [JsonPropertyName("releaseDate")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("backdrop_path")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public IReadOnlyList<string>? BackdropPath { get; set; }
}

public sealed class XtreamEpisode
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("episode_num")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("season")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Season { get; set; }

    [JsonPropertyName("title")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Title { get; set; }

    [JsonPropertyName("container_extension")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ContainerExtension { get; set; }

    [JsonPropertyName("info")]
    public XtreamEpisodeInfo? Info { get; set; }
}

public sealed class XtreamEpisodeInfo
{
    [JsonPropertyName("plot")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Plot { get; set; }

    [JsonPropertyName("duration_secs")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("movie_image")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? MovieImage { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Rating { get; set; }

    [JsonPropertyName("releasedate")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReleaseDate { get; set; }
}

public sealed class XtreamShortEpgResponse
{
    [JsonPropertyName("epg_listings")]
    public List<XtreamEpgListing>? EpgListings { get; set; }
}

public sealed class XtreamEpgListing
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Description { get; set; }

    [JsonPropertyName("start_timestamp")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? StartUnix { get; set; }

    [JsonPropertyName("stop_timestamp")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? StopUnix { get; set; }

    /// <summary>Titles arrive base64-encoded from most panels; decoded lazily.</summary>
    [JsonIgnore]
    public string? DecodedTitle => XtreamText.DecodeMaybeBase64(Title);

    [JsonIgnore]
    public string? DecodedDescription => XtreamText.DecodeMaybeBase64(Description);
}

/// <summary>Base64 helpers for Xtream's encoded EPG text fields.</summary>
public static class XtreamText
{
    /// <summary>
    /// Decodes a base64 payload if (and only if) it convincingly is one: valid alphabet,
    /// valid padding, and a printable UTF-8 result. Otherwise returns the input unchanged.
    /// </summary>
    public static string? DecodeMaybeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length % 4 != 0)
        {
            return value;
        }

        Span<byte> buffer = value.Length <= 512 ? stackalloc byte[512] : new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return value;
        }

        var decoded = Encoding.UTF8.GetString(buffer[..written]);
        foreach (var c in decoded)
        {
            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t')
            {
                return value;
            }

            if (c == '�')
            {
                return value;
            }
        }

        return decoded;
    }
}
