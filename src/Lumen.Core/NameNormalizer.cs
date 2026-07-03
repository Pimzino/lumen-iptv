using System.Globalization;
using System.Text;

namespace Lumen.Core;

/// <summary>
/// Normalizes channel names for fuzzy EPG matching and search: lowercase, diacritics folded,
/// punctuation dropped, quality suffixes (HD/FHD/4K/…) and playlist country prefixes stripped.
/// </summary>
public static class NameNormalizer
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "sd", "4k", "8k", "hq", "fullhd",
        "hevc", "h265", "h264", "raw", "vip", "backup", "plus",
    };

    /// <summary>Normalizes a display name. Returns an empty string for null/blank input.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = StripCountryPrefix(name.Trim());
        var folded = FoldToAsciiLower(text);
        var tokens = folded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !NoiseTokens.Contains(token))
            .ToArray();

        // If stripping noise removed everything ("HD"), fall back to the folded text.
        return tokens.Length > 0
            ? string.Join(' ', tokens)
            : folded.Trim();
    }

    /// <summary>Strips leading playlist prefixes like "UK:", "US |", "DE -".</summary>
    private static string StripCountryPrefix(string text)
    {
        for (var i = 0; i < text.Length && i <= 4; i++)
        {
            var c = text[i];
            if (c is ':' or '|')
            {
                // Only treat it as a prefix when everything before is letters (a region code).
                var head = text[..i].Trim();
                if (head.Length is >= 2 and <= 4 && head.All(char.IsLetter))
                {
                    return text[(i + 1)..].Trim();
                }

                return text;
            }
        }

        return text;
    }

    /// <summary>Lowercases, folds diacritics, and replaces punctuation with spaces.</summary>
    private static string FoldToAsciiLower(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(' ');
            }
        }

        // Collapse runs of spaces.
        var result = new StringBuilder(builder.Length);
        var previousWasSpace = false;
        foreach (var c in builder.ToString())
        {
            if (c == ' ')
            {
                if (!previousWasSpace && result.Length > 0)
                {
                    result.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                result.Append(c);
                previousWasSpace = false;
            }
        }

        return result.ToString().TrimEnd();
    }
}
