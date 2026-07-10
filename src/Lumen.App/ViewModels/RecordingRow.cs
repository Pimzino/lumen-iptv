using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.App.Resources;
using Lumen.Core.Models;

namespace Lumen.App.ViewModels;

/// <summary>
/// A live recording shown on the Recordings page and behind the player's Record toggle. The
/// <see cref="Services.Recordings.RecordingService"/> owns the instance and mutates it on the
/// UI thread; views bind to it directly (same pattern as <see cref="DownloadRow"/>).
/// </summary>
public sealed partial class RecordingRow : ObservableObject
{
    public RecordingRow(Recording recording)
    {
        Item = recording;
        _status = recording.Status;
        _sizeBytes = recording.SizeBytes;
        _elapsedSeconds = recording.DurationSeconds ?? 0;
        _error = recording.Error;
    }

    /// <summary>The backing record; carries identity, paths, and display metadata.</summary>
    public Recording Item { get; }

    public long Id => Item.Id;

    public string ChannelName => Item.ChannelName;

    public string? ProgrammeTitle => Item.ProgrammeTitle;

    public string? LogoUrl => Item.LogoUrl;

    public string FilePath => Item.FilePath;

    /// <summary>Display title: the user's rename, else the programme that was airing, else the channel.</summary>
    public string Title => !string.IsNullOrWhiteSpace(Item.CustomTitle)
        ? Item.CustomTitle!
        : string.IsNullOrWhiteSpace(ProgrammeTitle) ? ChannelName : ProgrammeTitle!;

    /// <summary>Lands a rename (UI thread; called by the recording service).</summary>
    internal void ApplyTitle(string? customTitle)
    {
        Item.CustomTitle = customTitle;
        OnPropertyChanged(nameof(Title));
    }

    public string Monogram => ChannelName.Length > 0 ? ChannelName[..1].ToUpperInvariant() : "?";

    /// <summary>"9 Jul 2026 · 21:00" — when the capture started.</summary>
    public string StartedLabel
    {
        get
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(Item.StartedUtc).ToLocalTime();
            return $"{local.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {local:HH:mm}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DownloadStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _sizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    [NotifyPropertyChangedFor(nameof(RecordLabel))]
    private long _elapsedSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    public bool IsRecording => Status == DownloadStatus.Downloading;

    public bool IsCompleted => Status == DownloadStatus.Completed;

    public bool IsFailed => Status == DownloadStatus.Failed;

    public bool CanPlay => IsCompleted && File.Exists(FilePath);

    /// <summary>"1:23:45" (or "12:34" under an hour) — live elapsed, or the final duration.</summary>
    public string ElapsedText
    {
        get
        {
            var span = TimeSpan.FromSeconds(ElapsedSeconds);
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    /// <summary>Player Record-toggle caption while this capture runs: "REC · 12:34".</summary>
    public string RecordLabel => $"REC · {ElapsedText}";

    public string SizeText => SizeBytes > 0 ? FormatBytes(SizeBytes) : string.Empty;

    /// <summary>Caption for the Recordings page row.</summary>
    public string StatusText => Status switch
    {
        DownloadStatus.Downloading => Strings.Recordings_InProgress,
        DownloadStatus.Completed => Strings.Recordings_Saved,
        DownloadStatus.Failed => string.IsNullOrWhiteSpace(Error)
            ? Strings.Downloads_Failed
            : $"{Strings.Downloads_Failed}: {Error}",
        _ => string.Empty,
    };

    /// <summary>Applies a live progress tick (UI thread; called by the recording service).</summary>
    internal void ApplyProgress(long elapsedSeconds, long sizeBytes)
    {
        ElapsedSeconds = elapsedSeconds;
        SizeBytes = sizeBytes;
    }

    /// <summary>Lands a terminal state (UI thread).</summary>
    internal void ApplyFinal(DownloadStatus status, string? error, long? durationSeconds, long sizeBytes)
    {
        Status = status;
        Error = error;
        if (durationSeconds is { } duration)
        {
            ElapsedSeconds = duration;
        }

        SizeBytes = sizeBytes;
        Item.Status = status;
        Item.Error = error;
        Item.DurationSeconds = durationSeconds;
        Item.SizeBytes = sizeBytes;
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
