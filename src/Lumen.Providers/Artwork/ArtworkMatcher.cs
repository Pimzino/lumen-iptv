using Lumen.Core;

namespace Lumen.Providers.Artwork;

/// <summary>
/// Scores metadata-service candidates against a query so providers only return artwork for
/// titles they are actually confident about — a wrong poster is worse than no poster.
/// </summary>
internal static class ArtworkMatcher
{
    /// <summary>Minimum score a candidate must reach to be used.</summary>
    public const int Threshold = 55;

    /// <summary>
    /// Scores a candidate. The query title is compared in two forms — as-is and with the year
    /// appended — because catalogs write both "Wonder Woman" (1984) and "Wonder Woman 1984".
    /// </summary>
    public static int Score(string candidateTitle, int? candidateYear, string queryTitle, int? queryYear)
    {
        var candidate = NameNormalizer.Normalize(candidateTitle);
        if (candidate.Length == 0)
        {
            return 0;
        }

        var query = NameNormalizer.Normalize(queryTitle);
        var queryWithYear = queryYear is { } y ? $"{query} {y}" : null;

        int score;
        if (queryWithYear is not null && candidate == queryWithYear)
        {
            score = 100;
        }
        else if (candidate == query)
        {
            score = 90;
        }
        else if (candidate.StartsWith(query, StringComparison.Ordinal) ||
                 query.StartsWith(candidate, StringComparison.Ordinal))
        {
            score = 62;
        }
        else
        {
            score = (int)(TokenOverlap(query, candidate) * 55);
        }

        if (queryYear is { } expected && candidateYear is { } actual)
        {
            score += Math.Abs(expected - actual) switch
            {
                0 => 8,
                1 => 4,
                _ => -10,
            };
        }

        return score;
    }

    private static double TokenOverlap(string query, string candidate)
    {
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length == 0)
        {
            return 0;
        }

        var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var hit = queryTokens.Count(candidateTokens.Contains);
        return (double)hit / queryTokens.Length;
    }

    /// <summary>Extracts a year from a metadata date string ("2019-05-30"), null-safe.</summary>
    public static int? YearOf(string? date) =>
        !string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var year)
            ? year
            : null;
}
