using System.Globalization;

namespace Lumen.Core.Updates;

/// <summary>
/// A semantic-ish version parsed from a GitHub release tag (e.g. <c>v1.2.3</c> or
/// <c>1.2.3-beta.1</c>). Comparison follows SemVer precedence: the numeric core is compared
/// component-by-component, and — when cores are equal — a final release outranks the same core
/// carrying a pre-release suffix. Tolerant of a leading <c>v</c>, 1–4 numeric components, and
/// trailing build metadata (<c>+abc</c>), which is ignored for ordering.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int[] _core;
    private readonly string[] _preRelease;

    private ReleaseVersion(int[] core, string[] preRelease, string raw)
    {
        _core = core;
        _preRelease = preRelease;
        Raw = raw;
    }

    /// <summary>The original text this version was parsed from.</summary>
    public string Raw { get; }

    /// <summary>Dot-separated pre-release identifiers; empty for a final release.</summary>
    public IReadOnlyList<string> PreReleaseIdentifiers => _preRelease;

    /// <summary>True when the version carries a pre-release suffix such as <c>-beta.1</c>.</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>Major (first) component, or 0 when absent.</summary>
    public int Major => _core.Length > 0 ? _core[0] : 0;

    /// <summary>Minor (second) component, or 0 when absent.</summary>
    public int Minor => _core.Length > 1 ? _core[1] : 0;

    /// <summary>Patch (third) component, or 0 when absent.</summary>
    public int Patch => _core.Length > 2 ? _core[2] : 0;

    /// <summary>Parses a version, throwing <see cref="FormatException"/> on malformed input.</summary>
    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a recognizable version.");

    /// <summary>
    /// Attempts to parse a version string. Accepts an optional leading <c>v</c>/<c>V</c>, 1–4
    /// dot-separated non-negative integers, an optional <c>-prerelease</c> suffix, and optional
    /// <c>+buildmetadata</c> (ignored). Returns false for anything else.
    /// </summary>
    public static bool TryParse(
        string? text,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim();
        var body = raw;
        if (body[0] is 'v' or 'V')
        {
            body = body[1..];
        }

        // Build metadata never affects precedence; drop it first.
        var plus = body.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            body = body[..plus];
        }

        var preRelease = Array.Empty<string>();
        var dash = body.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = body[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            body = body[..dash];
        }

        var parts = body.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        var core = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var component))
            {
                return false;
            }

            core[i] = component;
        }

        version = new ReleaseVersion(core, preRelease, raw);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var length = Math.Max(_core.Length, other._core.Length);
        for (var i = 0; i < length; i++)
        {
            var mine = i < _core.Length ? _core[i] : 0;
            var theirs = i < other._core.Length ? other._core[i] : 0;
            if (mine != theirs)
            {
                return mine < theirs ? -1 : 1;
            }
        }

        // Equal numeric cores: a final release has higher precedence than a pre-release.
        if (_preRelease.Length == 0 && other._preRelease.Length == 0)
        {
            return 0;
        }

        if (_preRelease.Length == 0)
        {
            return 1;
        }

        if (other._preRelease.Length == 0)
        {
            return -1;
        }

        return ComparePreRelease(_preRelease, other._preRelease);
    }

    // SemVer 2.0.0 §11: compare identifiers left to right; a larger set wins when all shared
    // identifiers are equal.
    private static int ComparePreRelease(string[] a, string[] b)
    {
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var result = CompareIdentifier(a[i], b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var aValue);
        var bNumeric = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bValue);

        if (aNumeric && bNumeric)
        {
            return aValue.CompareTo(bValue);
        }

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (aNumeric)
        {
            return -1;
        }

        if (bNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(a, b);
    }

    /// <inheritdoc />
    public bool Equals(ReleaseVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in _preRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns the normalized <c>major.minor.patch[-prerelease]</c> form.</summary>
    public override string ToString()
    {
        var core = string.Join('.', _core);
        return _preRelease.Length == 0 ? core : $"{core}-{string.Join('.', _preRelease)}";
    }

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);

    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) < 0;

    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) <= 0;

    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) > 0;

    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) >= 0;

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }
}
