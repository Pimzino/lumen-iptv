namespace Lumen.Core.Models;

/// <summary>Lifecycle state of an offline download/recording.</summary>
public enum DownloadStatus
{
    /// <summary>Enqueued, waiting for a worker slot.</summary>
    Queued = 0,

    /// <summary>Actively downloading (progressive) or recording (HLS).</summary>
    Downloading = 1,

    /// <summary>Paused by the user; the partial file is kept (progressive only).</summary>
    Paused = 2,

    /// <summary>Finished: the final file is in place and playable offline.</summary>
    Completed = 3,

    /// <summary>Stopped by an error; retryable.</summary>
    Failed = 4,
}
