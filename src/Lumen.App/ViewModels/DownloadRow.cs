using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.App.Resources;
using Lumen.App.Services.Downloads;
using Lumen.Core.Models;

namespace Lumen.App.ViewModels;

/// <summary>
/// A single download shown on the Downloads page and behind the detail-page download buttons.
/// The <see cref="Services.Downloads.DownloadService"/> owns the instance and mutates its
/// observable properties on the UI thread; views bind to it directly (like <see cref="EpisodeRow"/>).
/// </summary>
public sealed partial class DownloadRow : ObservableObject
{
    public DownloadRow(DownloadItem item)
    {
        Item = item;
        _status = item.Status;
        _downloadedBytes = item.DownloadedBytes;
        _totalBytes = item.TotalBytes;
        _progressPermille = item.ProgressPermille;
        _error = item.Error;
        _posterUrl = item.PosterUrl;
    }

    /// <summary>The backing record; carries identity, paths, and grouping metadata.</summary>
    public DownloadItem Item { get; }

    public long Id => Item.Id;

    public string ItemKey => Item.ItemKey;

    public ContentKind Kind => Item.Kind;

    public string? SeriesItemKey => Item.SeriesItemKey;

    public int? Season => Item.Season;

    public int? EpisodeNumber => Item.EpisodeNumber;

    public bool IsHls => Item.IsHls;

    public string Title => Item.Title;

    public string FilePath => Item.FilePath;

    [ObservableProperty]
    private string? _posterUrl;

    public string Monogram => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    private DownloadStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _downloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(HasKnownSize))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    private int _progressPermille;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    /// <summary>Progress on a 0–100 scale for the bar (permille ÷ 10).</summary>
    public double ProgressPercent => Math.Clamp(ProgressPermille / 10.0, 0, 100);

    public bool HasKnownSize => TotalBytes is > 0;

    public bool IsDownloading => Status == DownloadStatus.Downloading;

    public bool IsQueued => Status == DownloadStatus.Queued;

    public bool IsPaused => Status == DownloadStatus.Paused;

    public bool IsCompleted => Status == DownloadStatus.Completed;

    public bool IsFailed => Status == DownloadStatus.Failed;

    /// <summary>True for anything not yet completed — the Downloads page "in progress" section.</summary>
    public bool IsInProgress => Status is DownloadStatus.Queued or DownloadStatus.Downloading
        or DownloadStatus.Paused or DownloadStatus.Failed;

    public bool CanPlay => IsCompleted && File.Exists(FilePath);

    public bool CanPause => IsDownloading && !IsHls;

    public bool CanResume => IsPaused;

    public bool CanRetry => IsFailed;

    /// <summary>An HLS record (or a length-less stream) has no determinate percentage while running.</summary>
    public bool IsIndeterminate => (IsDownloading || IsQueued) && ProgressPermille <= 0;

    /// <summary>"120 MB / 1.2 GB" when the size is known, else the bytes downloaded so far.</summary>
    public string SizeText => HasKnownSize
        ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes!.Value)}"
        : DownloadedBytes > 0 ? FormatBytes(DownloadedBytes) : string.Empty;

    /// <summary>Status caption for the Downloads page row.</summary>
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Strings.Downloads_Queued,
        DownloadStatus.Downloading => IsIndeterminate
            ? Strings.Downloads_Downloading
            : Strings.Format(Strings.Downloads_DownloadingPercentFormat, (int)ProgressPercent),
        DownloadStatus.Paused => Strings.Downloads_Paused,
        DownloadStatus.Completed => Strings.Downloads_Downloaded,
        DownloadStatus.Failed => string.IsNullOrWhiteSpace(Error)
            ? Strings.Downloads_Failed
            : $"{Strings.Downloads_Failed}: {Error}",
        _ => string.Empty,
    };

    /// <summary>Label for the detail-page download button, reflecting this item's live state.</summary>
    public string ButtonLabel => Status switch
    {
        DownloadStatus.Queued => Strings.Downloads_Queued,
        DownloadStatus.Downloading => IsIndeterminate
            ? Strings.Downloads_Downloading
            : Strings.Format(Strings.Downloads_DownloadingPercentFormat, (int)ProgressPercent),
        DownloadStatus.Paused => Strings.Downloads_Resume,
        DownloadStatus.Completed => Strings.Downloads_Downloaded,
        DownloadStatus.Failed => Strings.Common_Retry,
        _ => Strings.Downloads_Download,
    };

    /// <summary>Applies a progress tick (UI thread; called by the download service).</summary>
    internal void ApplyProgress(DownloadProgress progress)
    {
        DownloadedBytes = progress.DownloadedBytes;
        TotalBytes = progress.TotalBytes;
        ProgressPermille = progress.ProgressPermille;
    }

    /// <summary>Re-reads mutable fields from a freshly loaded record (UI thread).</summary>
    internal void Sync(DownloadItem item)
    {
        Status = item.Status;
        DownloadedBytes = item.DownloadedBytes;
        TotalBytes = item.TotalBytes;
        ProgressPermille = item.ProgressPermille;
        Error = item.Error;
        Item.Status = item.Status;
        Item.DownloadedBytes = item.DownloadedBytes;
        Item.TotalBytes = item.TotalBytes;
        Item.ProgressPermille = item.ProgressPermille;
        Item.Error = item.Error;
        Item.CompletedUtc = item.CompletedUtc;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", size, units[unit]);
    }
}
