namespace Lumen.Core.Downloads;

/// <summary>How a resumed HTTP download should treat any existing partial file.</summary>
public enum ResumeMode
{
    /// <summary>Write from byte 0 — a fresh download, or the server ignored the range request.</summary>
    FromStart = 0,

    /// <summary>Append to the existing partial — the server honored the range (206).</summary>
    Append = 1,
}

/// <summary>The resume decision plus the resulting known total size (null when unknown/chunked).</summary>
public readonly record struct ResumePlan(ResumeMode Mode, long? TotalBytes);

/// <summary>
/// Pure decision for HTTP Range-resume: from how many bytes already exist on disk, the server's
/// status code, and the response Content-Length, decide whether to append or restart and what the
/// total size is. Dependency-free (no HTTP types) so it is unit-testable in isolation.
/// </summary>
public static class DownloadResume
{
    /// <param name="existingBytes">Bytes already in the partial file (0 for a fresh start).</param>
    /// <param name="statusCode">HTTP status of the (possibly ranged) GET.</param>
    /// <param name="contentLength">Response Content-Length; for a 206 this is the <b>remaining</b> length.</param>
    public static ResumePlan Decide(long existingBytes, int statusCode, long? contentLength)
    {
        // 416 Range Not Satisfiable: the partial is at or beyond the server's size — start clean.
        if (statusCode == 416)
        {
            return new ResumePlan(ResumeMode.FromStart, null);
        }

        if (existingBytes > 0 && statusCode == 206)
        {
            // A 206 Content-Length is the length of the remaining range, so the total is existing + it.
            var total = contentLength is { } remaining ? existingBytes + remaining : (long?)null;
            return new ResumePlan(ResumeMode.Append, total);
        }

        // existing>0 with 200: the server ignored the range and is resending the whole body → truncate.
        // existing==0: a fresh download. Either way write from the start; total is the Content-Length.
        return new ResumePlan(ResumeMode.FromStart, contentLength);
    }
}
