namespace Lumen.Core.Models;

/// <summary>
/// A provider account's status as reported at login — connection limits, subscription expiry,
/// and server clock. Populated from an Xtream <c>player_api.php</c> auth response; a live
/// snapshot, never persisted (the connection count is only meaningful fresh).
/// </summary>
public sealed class AccountInfo
{
    /// <summary>Panel-reported status: "Active", "Expired", "Banned", "Disabled"… (null if absent).</summary>
    public string? Status { get; init; }

    /// <summary>Subscription end, or null for a lifetime/no-expiry account.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Whether the account is a trial.</summary>
    public bool IsTrial { get; init; }

    /// <summary>Connections currently open on the account (live value).</summary>
    public int? ActiveConnections { get; init; }

    /// <summary>Maximum concurrent connections the account allows.</summary>
    public int? MaxConnections { get; init; }

    /// <summary>When the account was created, if reported.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Container formats the panel will serve (e.g. ts, m3u8, mp4).</summary>
    public IReadOnlyList<string> AllowedFormats { get; init; } = [];

    /// <summary>The panel's IANA timezone (e.g. "Europe/London"), if reported.</summary>
    public string? ServerTimezone { get; init; }

    /// <summary>The panel's current wall-clock string ("yyyy-MM-dd HH:mm:ss"), if reported.</summary>
    public string? ServerTimeNow { get; init; }

    /// <summary>True when the panel status is "Active".</summary>
    public bool IsActive => string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Free connection slots, or null when the limit/usage is unknown.</summary>
    public int? ConnectionsAvailable =>
        MaxConnections is { } max && ActiveConnections is { } active ? Math.Max(0, max - active) : null;

    /// <summary>
    /// True when every allowed connection is in use — the provider will refuse the next stream
    /// (the 503 / connection-refused class of playback failure).
    /// </summary>
    public bool AllConnectionsInUse =>
        MaxConnections is { } max && max > 0 && ActiveConnections is { } active && active >= max;
}
