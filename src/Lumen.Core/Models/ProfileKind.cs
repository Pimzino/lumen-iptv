namespace Lumen.Core.Models;

/// <summary>The kind of service a profile connects to.</summary>
public enum ProfileKind
{
    /// <summary>Xtream Codes portal (server + username + password).</summary>
    Xtream = 0,

    /// <summary>M3U / M3U8 playlist (URL or local file).</summary>
    M3u = 1,
}
