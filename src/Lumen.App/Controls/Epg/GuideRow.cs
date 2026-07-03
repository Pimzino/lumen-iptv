using Lumen.Core.Models;

namespace Lumen.App.Controls.Epg;

/// <summary>One channel's lane in the EPG grid: the channel plus its ordered programmes.</summary>
public sealed class GuideRow
{
    public GuideRow(Channel channel, string? xmltvId, IReadOnlyList<Programme> programmes)
    {
        Channel = channel;
        XmltvId = xmltvId;
        Programmes = programmes;
    }

    public Channel Channel { get; }

    public string? XmltvId { get; }

    /// <summary>Programmes ordered by start time. May be empty when the channel has no guide data.</summary>
    public IReadOnlyList<Programme> Programmes { get; }

    public string Monogram => Channel.Name.Length > 0 ? Channel.Name[..1].ToUpperInvariant() : "?";
}
