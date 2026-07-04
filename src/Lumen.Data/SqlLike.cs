namespace Lumen.Data;

/// <summary>
/// Escapes user input for embedding in a SQL LIKE pattern; queries must pair the pattern
/// with <c>ESCAPE '\'</c>. Callers add their own wildcards around the escaped text.
/// </summary>
internal static class SqlLike
{
    public static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
