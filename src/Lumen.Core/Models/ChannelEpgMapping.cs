namespace Lumen.Core.Models;

/// <summary>Resolved mapping from a playlist channel to an XMLTV channel id.</summary>
public sealed class ChannelEpgMapping
{
    public long ChannelId { get; set; }

    public string XmltvId { get; set; } = string.Empty;

    /// <summary>True when the user mapped this channel by hand; manual mappings survive refreshes.</summary>
    public bool IsManual { get; set; }
}
