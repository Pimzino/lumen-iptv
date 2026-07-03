namespace Lumen.Core.Models;

/// <summary>A channel declared by an XMLTV document.</summary>
public sealed class EpgChannel
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>The XMLTV channel id programmes reference.</summary>
    public string XmltvId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? IconUrl { get; set; }
}
