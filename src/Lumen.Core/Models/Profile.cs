namespace Lumen.Core.Models;

/// <summary>A configured service account (Xtream portal or M3U playlist) plus its EPG source.</summary>
public sealed class Profile
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ProfileKind Kind { get; set; }

    /// <summary>Xtream portal base URL (null for M3U profiles).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Xtream username (null for M3U profiles).</summary>
    public string? Username { get; set; }

    /// <summary>DPAPI-protected Xtream password; never stored in plaintext.</summary>
    public byte[]? PasswordProtected { get; set; }

    /// <summary>M3U playlist URL or local file path (null for Xtream profiles).</summary>
    public string? PlaylistSource { get; set; }

    public bool PlaylistIsFile { get; set; }

    /// <summary>XMLTV source URL or file path. For Xtream profiles this may be empty (auto-discovered).</summary>
    public string? EpgSource { get; set; }

    public bool EpgIsFile { get; set; }

    /// <summary>Prefer HLS (.m3u8) containers over MPEG-TS for Xtream live streams.</summary>
    public bool PreferHls { get; set; }

    /// <summary>
    /// User-Agent sent on stream requests. Many IPTV panels whitelist a specific player's UA and
    /// reject everything else with HTTP 403. Null means "use <see cref="DefaultStreamUserAgent"/>".
    /// </summary>
    public string? StreamUserAgent { get; set; }

    /// <summary>
    /// Default stream User-Agent when a profile doesn't override it. Matches the widely-accepted
    /// IPTV Smarters client, which most panels allow.
    /// </summary>
    public const string DefaultStreamUserAgent = "IPTVSmartersPlayer";

    /// <summary>Accent color used for the profile avatar, as #RRGGBB.</summary>
    public string? AvatarColor { get; set; }

    /// <summary>Creation time, unix seconds (UTC).</summary>
    public long CreatedUtc { get; set; }

    /// <summary>Last activation time, unix seconds (UTC).</summary>
    public long? LastUsedUtc { get; set; }
}
