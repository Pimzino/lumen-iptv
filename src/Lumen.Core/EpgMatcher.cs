using Lumen.Core.Models;

namespace Lumen.Core;

/// <summary>
/// Maps playlist channels to XMLTV channel ids: tvg-id exact match first, then
/// normalized-name matching against EPG display names and dotted XMLTV ids.
/// </summary>
public static class EpgMatcher
{
    /// <summary>Computes automatic mappings. Channels without a confident match are omitted.</summary>
    public static IReadOnlyList<ChannelEpgMapping> Match(
        IReadOnlyList<Channel> channels, IReadOnlyList<EpgChannel> epgChannels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(epgChannels);

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var epg in epgChannels)
        {
            if (string.IsNullOrWhiteSpace(epg.XmltvId))
            {
                continue;
            }

            byId.TryAdd(epg.XmltvId, epg.XmltvId);

            var normalizedName = NameNormalizer.Normalize(epg.DisplayName);
            if (normalizedName.Length > 0)
            {
                byName.TryAdd(normalizedName, epg.XmltvId);
            }

            // XMLTV ids are often dotted names: "bbc.one.uk" — usable as a name source.
            var normalizedId = NameNormalizer.Normalize(epg.XmltvId.Replace('.', ' '));
            if (normalizedId.Length > 0)
            {
                byName.TryAdd(normalizedId, epg.XmltvId);
            }
        }

        var mappings = new List<ChannelEpgMapping>(channels.Count);
        foreach (var channel in channels)
        {
            var xmltvId = ResolveXmltvId(channel, byId, byName);
            if (xmltvId is not null)
            {
                mappings.Add(new ChannelEpgMapping
                {
                    ChannelId = channel.Id,
                    XmltvId = xmltvId,
                    IsManual = false,
                });
            }
        }

        return mappings;
    }

    private static string? ResolveXmltvId(
        Channel channel,
        Dictionary<string, string> byId,
        Dictionary<string, string> byName)
    {
        if (!string.IsNullOrWhiteSpace(channel.EpgChannelId) &&
            byId.TryGetValue(channel.EpgChannelId, out var exact))
        {
            return exact;
        }

        var normalized = NameNormalizer.Normalize(channel.Name);
        if (normalized.Length > 0 && byName.TryGetValue(normalized, out var fuzzy))
        {
            return fuzzy;
        }

        return null;
    }
}
