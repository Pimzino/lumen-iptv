using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumen.Providers.M3u;

/// <summary>
/// Tolerant streaming parser: BOM, CRLF/LF, missing or unquoted attributes, comment lines,
/// #EXTGRP (sticky group), and #EXTVLCOPT (user-agent/referrer for the next entry) are all handled.
/// </summary>
public sealed class M3uPlaylistParser : IM3uPlaylistParser
{
    public async IAsyncEnumerable<M3uEntry> ParseAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);

        ExtInf? pending = null;
        string? stickyGroup = null;
        string? nextUserAgent = null;
        string? nextReferrer = null;
        var firstLine = true;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } rawLine)
        {
            var line = rawLine.Trim();
            if (firstLine)
            {
                line = line.TrimStart('﻿').Trim();
                firstLine = false;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    pending = ParseExtInf(line);
                }
                else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                {
                    var group = line[8..].Trim();
                    stickyGroup = group.Length > 0 ? group : null;
                }
                else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseVlcOpt(line[11..], ref nextUserAgent, ref nextReferrer);
                }

                // #EXTM3U, #EXT-X-*, and plain comments are ignored.
                continue;
            }

            // Any other non-empty line is a stream URL.
            var url = line;
            var info = pending ?? ExtInf.Empty;
            var title = info.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = info.Attributes.GetValueOrDefault("tvg-name");
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = TitleFromUrl(url);
            }

            yield return new M3uEntry
            {
                Title = title!,
                Url = url,
                TvgId = NullIfEmpty(info.Attributes.GetValueOrDefault("tvg-id")),
                TvgName = NullIfEmpty(info.Attributes.GetValueOrDefault("tvg-name")),
                LogoUrl = NullIfEmpty(info.Attributes.GetValueOrDefault("tvg-logo")),
                GroupTitle = NullIfEmpty(info.Attributes.GetValueOrDefault("group-title")) ?? stickyGroup,
                TvgShiftMinutes = ParseShiftMinutes(info.Attributes.GetValueOrDefault("tvg-shift")),
                CatchupType = NullIfEmpty(
                    info.Attributes.GetValueOrDefault("catchup")
                    ?? info.Attributes.GetValueOrDefault("catchup-type")),
                UserAgent = nextUserAgent,
                Referrer = nextReferrer,
                DurationSeconds = info.DurationSeconds,
            };

            pending = null;
            nextUserAgent = null;
            nextReferrer = null;
        }
    }

    private static void ParseVlcOpt(string option, ref string? userAgent, ref string? referrer)
    {
        var separator = option.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        var key = option[..separator].Trim();
        var value = option[(separator + 1)..].Trim().Trim('"');
        if (value.Length == 0)
        {
            return;
        }

        if (key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase))
        {
            userAgent = value;
        }
        else if (key.Equals("http-referrer", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("http-referer", StringComparison.OrdinalIgnoreCase))
        {
            referrer = value;
        }
    }

    private static ExtInf ParseExtInf(string line)
    {
        var pos = "#EXTINF:".Length;

        // Duration: up to the first space or comma. Tolerates junk (defaults to -1).
        var durationStart = pos;
        while (pos < line.Length && line[pos] != ' ' && line[pos] != ',')
        {
            pos++;
        }

        double.TryParse(
            line.AsSpan(durationStart, pos - durationStart),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var duration);

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? title = null;

        while (pos < line.Length)
        {
            var c = line[pos];
            if (c is ' ' or '\t')
            {
                pos++;
                continue;
            }

            if (c == ',')
            {
                title = line[(pos + 1)..].Trim();
                break;
            }

            // key=value (value quoted, or unquoted until whitespace/comma)
            var keyStart = pos;
            while (pos < line.Length && line[pos] is not ('=' or ',' or ' '))
            {
                pos++;
            }

            if (pos >= line.Length || line[pos] != '=')
            {
                continue; // stray token — skip it
            }

            var key = line[keyStart..pos].Trim();
            pos++;

            string value;
            if (pos < line.Length && line[pos] == '"')
            {
                pos++;
                var valueStart = pos;
                while (pos < line.Length && line[pos] != '"')
                {
                    pos++;
                }

                value = line[valueStart..Math.Min(pos, line.Length)];
                if (pos < line.Length)
                {
                    pos++; // closing quote
                }
            }
            else
            {
                var valueStart = pos;
                while (pos < line.Length && line[pos] is not (' ' or ','))
                {
                    pos++;
                }

                value = line[valueStart..pos];
            }

            if (key.Length > 0)
            {
                attributes[key] = value.Trim();
            }
        }

        return new ExtInf(duration <= 0 ? duration : duration, title, attributes);
    }

    private static int ParseShiftMinutes(string? shift)
    {
        if (string.IsNullOrWhiteSpace(shift))
        {
            return 0;
        }

        // tvg-shift is in hours ("+2", "-1.5").
        return double.TryParse(shift.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            ? (int)Math.Round(hours * 60)
            : 0;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string TitleFromUrl(string url)
    {
        var span = url.AsSpan();
        var query = span.IndexOf('?');
        if (query >= 0)
        {
            span = span[..query];
        }

        span = span.TrimEnd('/');
        var slash = span.LastIndexOf('/');
        var tail = slash >= 0 ? span[(slash + 1)..] : span;
        return tail.Length > 0 ? Uri.UnescapeDataString(tail.ToString()) : url;
    }

    private sealed record ExtInf(double DurationSeconds, string? Title, Dictionary<string, string> Attributes)
    {
        public static readonly ExtInf Empty = new(-1, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
