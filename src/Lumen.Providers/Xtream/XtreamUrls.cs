namespace Lumen.Providers.Xtream;

/// <summary>Preferred container for Xtream live streams.</summary>
public enum LiveStreamContainer
{
    /// <summary>MPEG transport stream (.ts) — the Xtream default.</summary>
    MpegTs = 0,

    /// <summary>HTTP Live Streaming (.m3u8).</summary>
    Hls = 1,
}

/// <summary>
/// Builds Xtream Codes portal URLs: player API calls, stream endpoints, and the XMLTV feed.
/// All user-supplied segments are escaped; server input tolerates missing schemes and
/// trailing slashes.
/// </summary>
public static class XtreamUrls
{
    /// <summary>
    /// Normalizes a user-entered server address to <c>scheme://host[:port][/path]</c> with no
    /// trailing slash. A missing scheme defaults to http, matching common portal configurations.
    /// </summary>
    /// <exception cref="FormatException">The value cannot be parsed as an HTTP(S) URL.</exception>
    public static string NormalizeServerBase(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        var candidate = server.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"'{server}' is not a valid Xtream server URL.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Builds a player_api.php URL, optionally with an action parameter.</summary>
    public static Uri PlayerApi(string server, string username, string password, string? action = null)
    {
        var url = $"{NormalizeServerBase(server)}/player_api.php" +
                  $"?username={Uri.EscapeDataString(RequireCredential(username))}" +
                  $"&password={Uri.EscapeDataString(RequireCredential(password))}";
        if (!string.IsNullOrEmpty(action))
        {
            url += $"&action={Uri.EscapeDataString(action)}";
        }

        return new Uri(url);
    }

    /// <summary>Builds the portal's XMLTV EPG endpoint.</summary>
    public static Uri Xmltv(string server, string username, string password) =>
        new($"{NormalizeServerBase(server)}/xmltv.php" +
            $"?username={Uri.EscapeDataString(RequireCredential(username))}" +
            $"&password={Uri.EscapeDataString(RequireCredential(password))}");

    /// <summary>Builds a live stream URL: <c>{server}/live/{user}/{pass}/{id}.{ts|m3u8}</c>.</summary>
    public static Uri Live(
        string server,
        string username,
        string password,
        string streamId,
        LiveStreamContainer container = LiveStreamContainer.MpegTs)
    {
        var extension = container == LiveStreamContainer.Hls ? "m3u8" : "ts";
        return BuildStreamUri(server, "live", username, password, streamId, extension);
    }

    /// <summary>
    /// Builds a catch-up (timeshift) stream URL:
    /// <c>{server}/timeshift/{user}/{pass}/{durationMinutes}/{yyyy-MM-dd:HH-mm}/{id}.ts</c>.
    /// <paramref name="start"/> must already be in the <b>panel's</b> local time (see
    /// <see cref="XtreamServerTime.ToServerLocal"/>) — portals interpret it in their own zone.
    /// </summary>
    public static Uri Timeshift(
        string server, string username, string password, string streamId, DateTime start, int durationMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationMinutes, 1);

        var stamp = start.ToString("yyyy-MM-dd:HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        return new Uri(
            $"{NormalizeServerBase(server)}/timeshift" +
            $"/{Uri.EscapeDataString(RequireCredential(username))}" +
            $"/{Uri.EscapeDataString(RequireCredential(password))}" +
            $"/{durationMinutes}/{stamp}/{Uri.EscapeDataString(streamId)}.ts");
    }

    /// <summary>Builds a movie stream URL using the container extension reported by the API.</summary>
    public static Uri Movie(string server, string username, string password, string streamId, string? containerExtension) =>
        BuildStreamUri(server, "movie", username, password, streamId, NormalizeExtension(containerExtension));

    /// <summary>Builds a series-episode stream URL using the container extension reported by the API.</summary>
    public static Uri SeriesEpisode(string server, string username, string password, string episodeId, string? containerExtension) =>
        BuildStreamUri(server, "series", username, password, episodeId, NormalizeExtension(containerExtension));

    private static Uri BuildStreamUri(
        string server, string segment, string username, string password, string streamId, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        return new Uri(
            $"{NormalizeServerBase(server)}/{segment}" +
            $"/{Uri.EscapeDataString(RequireCredential(username))}" +
            $"/{Uri.EscapeDataString(RequireCredential(password))}" +
            $"/{Uri.EscapeDataString(streamId)}.{extension}");
    }

    private static string RequireCredential(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private static string NormalizeExtension(string? extension)
    {
        var trimmed = extension?.Trim().TrimStart('.');
        return string.IsNullOrEmpty(trimmed) ? "mp4" : trimmed;
    }
}
