using System.IO.Compression;
using System.Xml;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumen.Providers.Xmltv;

/// <summary>Streaming XMLTV EPG parser feeding an <see cref="IEpgImportSink"/>.</summary>
public interface IXmltvParser
{
    /// <summary>
    /// Parses an XMLTV document (gzip detected transparently) and streams channels and
    /// programmes into the sink. Progress is reported periodically.
    /// </summary>
    Task<EpgImportResult> ParseAsync(
        Stream stream,
        IEpgImportSink sink,
        IProgress<EpgImportProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// XmlReader-based implementation: constant memory regardless of document size (multi-GB
/// files are fine), malformed entries skipped and counted rather than fatal.
/// </summary>
public sealed class XmltvParser : IXmltvParser
{
    private const int ProgressInterval = 5000;

    private readonly ILogger<XmltvParser> _logger;

    public XmltvParser(ILogger<XmltvParser> logger)
    {
        _logger = logger;
    }

    public async Task<EpgImportResult> ParseAsync(
        Stream stream,
        IEpgImportSink sink,
        IProgress<EpgImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sink);

        var content = await WrapIfGzipAsync(stream, cancellationToken).ConfigureAwait(false);

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };

        long channels = 0;
        long programmes = 0;
        long skipped = 0;

        await sink.BeginAsync(cancellationToken).ConfigureAwait(false);

        using (var reader = XmlReader.Create(content, settings))
        {
            var advance = true;
            while (true)
            {
                if (advance && !await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                advance = true;

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "programme")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (programme, consumed) = await ReadProgrammeAsync(reader).ConfigureAwait(false);
                    advance = !consumed;
                    if (programme is not null)
                    {
                        await sink.AddProgrammeAsync(programme, cancellationToken).ConfigureAwait(false);
                        programmes++;
                        if (programmes % ProgressInterval == 0)
                        {
                            progress?.Report(new EpgImportProgress(channels, programmes));
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else if (reader.LocalName == "channel")
                {
                    var (channel, consumed) = await ReadChannelAsync(reader).ConfigureAwait(false);
                    advance = !consumed;
                    if (channel is not null)
                    {
                        await sink.AddChannelAsync(channel, cancellationToken).ConfigureAwait(false);
                        channels++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }

        await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new EpgImportProgress(channels, programmes));

        if (skipped > 0)
        {
            _logger.LogWarning("XMLTV import skipped {Skipped} malformed entries", skipped);
        }

        return new EpgImportResult(channels, programmes, skipped);
    }

    /// <summary>Reads a &lt;channel&gt; element. Returns (channel, readerAlreadyAdvanced).</summary>
    private static async Task<(EpgChannel? Channel, bool Consumed)> ReadChannelAsync(XmlReader reader)
    {
        var id = reader.GetAttribute("id");
        string? displayName = null;
        string? icon = null;

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            var advance = true;
            while (true)
            {
                if (advance && !await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                advance = true;

                if (reader.Depth <= depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "display-name" when displayName is null:
                        displayName = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        advance = false;
                        break;
                    case "icon" when icon is null:
                        icon = reader.GetAttribute("src");
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return (null, false);
        }

        return (new EpgChannel
        {
            XmltvId = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            IconUrl = string.IsNullOrWhiteSpace(icon) ? null : icon,
        }, false);
    }

    /// <summary>Reads a &lt;programme&gt; element. Returns (programme, readerAlreadyAdvanced).</summary>
    private static async Task<(Programme? Programme, bool Consumed)> ReadProgrammeAsync(XmlReader reader)
    {
        var startRaw = reader.GetAttribute("start");
        var stopRaw = reader.GetAttribute("stop");
        var channelId = reader.GetAttribute("channel");

        string? title = null;
        string? description = null;
        string? category = null;
        string? episode = null;
        string? icon = null;

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            var advance = true;
            while (true)
            {
                if (advance && !await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                advance = true;

                if (reader.Depth <= depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "title" when title is null:
                        title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        advance = false;
                        break;
                    case "desc" when description is null:
                        description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        advance = false;
                        break;
                    case "category" when category is null:
                        category = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        advance = false;
                        break;
                    case "episode-num" when episode is null:
                        episode = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        advance = false;
                        break;
                    case "icon" when icon is null:
                        icon = reader.GetAttribute("src");
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(title) ||
            !XmltvTime.TryParse(startRaw, out var startUnix) ||
            !XmltvTime.TryParse(stopRaw, out var stopUnix) ||
            stopUnix <= startUnix)
        {
            return (null, false);
        }

        return (new Programme
        {
            ChannelXmltvId = channelId,
            StartUtc = startUnix,
            StopUtc = stopUnix,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            EpisodeNumber = string.IsNullOrWhiteSpace(episode) ? null : episode.Trim(),
            IconUrl = string.IsNullOrWhiteSpace(icon) ? null : icon,
        }, false);
    }

    /// <summary>Sniffs gzip magic bytes and wraps in a decompressor when present.</summary>
    private static async Task<Stream> WrapIfGzipAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[2];
        var read = 0;
        while (read < 2)
        {
            var n = await stream.ReadAsync(prefix.AsMemory(read, 2 - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Stream replayed = new PrefixedStream(prefix[..read], stream);
        if (read == 2 && prefix[0] == 0x1F && prefix[1] == 0x8B)
        {
            return new GZipStream(replayed, CompressionMode.Decompress);
        }

        return replayed;
    }
}
