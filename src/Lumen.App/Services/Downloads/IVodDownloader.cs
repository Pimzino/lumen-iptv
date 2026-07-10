namespace Lumen.App.Services.Downloads;

/// <summary>A resolved, ready-to-run download job handed to a strategy.</summary>
/// <param name="Url">The resolved stream URL (Xtream endpoint or M3U-direct).</param>
/// <param name="UserAgent">Stream User-Agent to send (avoids provider 403s).</param>
/// <param name="PartPath">Where to write the growing partial file.</param>
/// <param name="FinalPath">Where the finished file lands (atomic move target).</param>
/// <param name="IsHls">True for HLS recording, false for a progressive HTTP download.</param>
public sealed record DownloadContext(string Url, string UserAgent, string PartPath, string FinalPath, bool IsHls);

/// <summary>A progress tick from a running download.</summary>
/// <param name="DownloadedBytes">Bytes written so far (progressive; 0 for HLS).</param>
/// <param name="TotalBytes">Total size when known (progressive with Content-Length); null otherwise.</param>
/// <param name="ProgressPermille">Uniform 0–1000 progress both strategies report.</param>
public sealed record DownloadProgress(long DownloadedBytes, long? TotalBytes, int ProgressPermille);

/// <summary>One download/record strategy: progressive HTTP or HLS stream-output recording.</summary>
public interface IVodDownloader
{
    /// <summary>
    /// Runs one job to completion, reporting progress as it goes. Honors
    /// <paramref name="cancellationToken"/> for pause/cancel. Returns once the final file is in
    /// place; throws on failure (including <see cref="HlsHandoffException"/> for a misrouted job).
    /// </summary>
    Task RunAsync(DownloadContext context, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by the progressive downloader when the response is actually an HLS playlist, so the
/// orchestrator can re-dispatch the job through the HLS recorder.
/// </summary>
public sealed class HlsHandoffException : Exception
{
    public HlsHandoffException(string message)
        : base(message)
    {
    }

    public HlsHandoffException()
    {
    }

    public HlsHandoffException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
