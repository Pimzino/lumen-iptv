using System.Text.RegularExpressions;
using Lumen.Core.Models;

namespace Lumen.Providers.M3u;

/// <summary>
/// Heuristic Live/Movie/Series classification for M3U entries, based on the stream URL
/// extension and group-title/name patterns. Users can override per group.
/// </summary>
public static partial class M3uContentClassifier
{
    [GeneratedRegex(@"\bS\d{1,2}\s?E\d{1,3}\b|\b\d{1,2}x\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesEpisodePattern();

    private static readonly string[] MovieExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v"];
    private static readonly string[] MovieGroupMarkers = ["movie", "movies", "vod", "film", "films", "cinema"];
    private static readonly string[] SeriesGroupMarkers = ["series", "show", "shows", "box set", "boxset"];

    public static ContentKind Classify(M3uEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var group = entry.GroupTitle ?? string.Empty;

        if (SeriesEpisodePattern().IsMatch(entry.Title) || ContainsAny(group, SeriesGroupMarkers))
        {
            return ContentKind.Series;
        }

        var extension = ExtensionOf(entry.Url);

        if (MovieExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return ContainsAny(group, SeriesGroupMarkers) ? ContentKind.Series : ContentKind.Movie;
        }

        if (ContainsAny(group, MovieGroupMarkers))
        {
            // File-style content in a movie group, regardless of extension.
            return ContentKind.Movie;
        }

        // .ts, .m3u8, extensionless, and everything else defaults to live.
        return ContentKind.Live;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtensionOf(string url)
    {
        var span = url.AsSpan();
        var query = span.IndexOfAny('?', '#');
        if (query >= 0)
        {
            span = span[..query];
        }

        var slash = span.LastIndexOf('/');
        var segment = slash >= 0 ? span[(slash + 1)..] : span;
        var dot = segment.LastIndexOf('.');
        return dot >= 0 ? segment[dot..].ToString() : string.Empty;
    }
}
