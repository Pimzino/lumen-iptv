using System.Text.RegularExpressions;

namespace Lumen.Core;

/// <summary>A display title reduced to something a metadata service can search.</summary>
public readonly record struct CleanTitle(string Title, int? Year);

/// <summary>
/// Reduces messy IPTV catalog names ("EN| The.Matrix.(1999) [4K HEVC]") to a searchable
/// title + year for external artwork lookups. Distinct from <see cref="NameNormalizer"/>,
/// which folds names for fuzzy matching — this preserves human casing and word shape.
/// </summary>
public static partial class TitleCleaner
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "4k", "8k", "uhd", "fhd", "hd", "sd", "hq", "hdr", "hdr10", "dv", "sdr",
        "720p", "1080p", "2160p", "480p", "60fps", "50fps",
        "h264", "h265", "x264", "x265", "hevc", "avc", "av1", "10bit",
        "webrip", "webdl", "web-dl", "web", "bluray", "brrip", "bdrip", "dvdrip", "hdrip",
        "camrip", "cam", "hdcam", "ts", "hdts", "remux", "imax",
        "multi", "multisub", "multi-sub", "multi-audio", "dual", "dubbed", "dub", "sub", "subbed",
        "vostfr", "vosta", "latino", "castellano", "dublado", "legendado",
        "extended", "unrated", "remastered", "uncut", "vip",
    };

    [GeneratedRegex(@"[\[{][^\]}]*[\]}]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\(\s*(19|20)\d{2}\s*\)")]
    private static partial Regex ParenYearRegex();

    [GeneratedRegex(@"^\s*[A-Za-z0-9+#]{1,6}\s*[|:•]\s*")]
    private static partial Regex PrefixTagRegex();

    [GeneratedRegex(@"^\s*[A-Z0-9+#]{2,6}\s*-\s+")]
    private static partial Regex DashPrefixTagRegex();

    [GeneratedRegex(@"\b(?:s\d{1,2}(?:\s*e\d{1,3})?|season\s+\d{1,2}|temporada\s+\d{1,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeasonRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>Cleans a raw catalog display name. Falls back to the trimmed input when cleaning would erase it.</summary>
    public static CleanTitle Clean(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return new CleanTitle(string.Empty, null);
        }

        var text = rawName.Trim();

        // Bracketed segments are always tags ([4K], {MULTI-SUB}), never title words.
        text = BracketedRegex().Replace(text, " ");

        // A parenthesized year is an unambiguous year marker.
        int? year = null;
        var parenYear = ParenYearRegex().Match(text);
        if (parenYear.Success && TryParseYear(parenYear.Value.Trim('(', ')', ' '), out var parsed))
        {
            year = parsed;
            text = text.Remove(parenYear.Index, parenYear.Length);
        }

        // Leading playlist tags ("EN|", "NF:", "VOD -"), possibly stacked.
        for (var i = 0; i < 3; i++)
        {
            var next = PrefixTagRegex().Replace(text, string.Empty);
            next = DashPrefixTagRegex().Replace(next, string.Empty);
            if (next == text)
            {
                break;
            }

            text = next;
        }

        // Dot/underscore-separated release names ("The.Big.Lebowski") — only when the name has
        // almost no real spaces, so titles like "S.W.A.T." survive normal playlists.
        if (text.Count(c => c == '.') >= 2 && text.Count(c => c == ' ') < 2)
        {
            text = text.Replace('.', ' ');
        }

        text = text.Replace('_', ' ');

        // Trailing season/episode markers on series names.
        text = TrailingSeasonRegex().Replace(text, string.Empty);

        // Quality/codec noise tokens anywhere in the name.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !NoiseTokens.Contains(t.Trim('-', '–', ':', ';', ',')))
            .ToList();

        // A bare trailing year ("Movie Name 1999") becomes the year hint — but only when other
        // words remain, so titles that ARE years ("1917") stay intact.
        if (year is null && tokens.Count >= 2 && TryParseYear(tokens[^1], out var trailing))
        {
            year = trailing;
            tokens.RemoveAt(tokens.Count - 1);
        }

        var title = string.Join(' ', tokens).Trim('-', '–', ':', ';', ',', ' ');
        title = WhitespaceRunRegex().Replace(title, " ");

        return title.Length > 0
            ? new CleanTitle(title, year)
            : new CleanTitle(rawName.Trim(), year);
    }

    private static bool TryParseYear(string token, out int year)
    {
        year = 0;
        if (token.Length == 4 && int.TryParse(token, out var value)
            && value >= 1900 && value <= DateTime.UtcNow.Year + 1)
        {
            year = value;
            return true;
        }

        return false;
    }
}
