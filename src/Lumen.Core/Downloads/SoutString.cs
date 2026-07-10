namespace Lumen.Core.Downloads;

/// <summary>
/// Builds the LibVLC stream-output (<c>sout</c>) media option that records a stream to a local
/// MPEG-TS file. Kept pure and dependency-free so its path handling is unit-testable without
/// LibVLC. TS is used because a truncated <c>.ts</c> is still a valid, playable stream.
/// </summary>
public static class SoutString
{
    /// <summary>
    /// Produces <c>:sout=#std{access=file,mux=ts,dst='&lt;path&gt;'}</c>. The destination is
    /// written with forward slashes (accepted on Windows, and avoiding backslash escaping in the
    /// config-chain parser) and wrapped in single quotes so spaces or commas in the path do not
    /// split the option chain. Callers must ensure the path contains no single quotes.
    /// </summary>
    public static string BuildFileRecord(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var dst = filePath.Replace('\\', '/');
        return $":sout=#std{{access=file,mux=ts,dst='{dst}'}}";
    }
}
