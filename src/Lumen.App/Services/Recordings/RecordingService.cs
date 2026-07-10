using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Services.Downloads;
using Lumen.App.Services.Playback;
using Lumen.App.ViewModels;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Recordings;

/// <summary>Why a record request did not start.</summary>
public enum RecordStartStatus
{
    Started = 0,

    /// <summary>Another recording is running (v1 allows one at a time).</summary>
    Busy = 1,

    /// <summary>The channel's stream URL could not be resolved under the active profile.</summary>
    Unresolvable = 2,
}

/// <summary>Outcome of a record request; <c>BusyChannelName</c> names the blocking recording.</summary>
public sealed record RecordStartOutcome(RecordStartStatus Status, RecordingRow? Row, string? BusyChannelName);

/// <summary>
/// Owns live TV recording for the whole app: at most one active capture (a second provider
/// connection through a dedicated headless LibVLC pipeline), SQLite-persisted rows, and a live
/// <see cref="ObservableCollection{T}"/> of <see cref="RecordingRow"/> the UI binds to. Stopping
/// (user, app exit, safety cap) finalizes the file — a live moment cannot be re-fetched.
/// </summary>
public sealed partial class RecordingService : ObservableObject, IHostedService
{
    private readonly IRecordingRepository _repo;
    private readonly PlaybackService _playback;
    private readonly LiveTsRecorder _recorder;
    private readonly IMessenger _messenger;
    private readonly IClock _clock;
    private readonly ILogger<RecordingService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdownCts = new();

    private ActiveJob? _activeJob;

    public RecordingService(
        IRecordingRepository repo,
        PlaybackService playback,
        LiveTsRecorder recorder,
        IMessenger messenger,
        IClock clock,
        ILogger<RecordingService> logger)
    {
        _repo = repo;
        _playback = playback;
        _recorder = recorder;
        _messenger = messenger;
        _clock = clock;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>Every recording the service knows about (all profiles); pages filter by profile.</summary>
    public ObservableCollection<RecordingRow> Recordings { get; } = [];

    /// <summary>The capture currently running; null when idle. Bound by the player overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    private RecordingRow? _activeRecording;

    public bool IsRecording => ActiveRecording is not null;

    // ---- Lifecycle (IHostedService) ---------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ReconcileInterruptedAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel(); // the recorder finalizes the file on cancellation
        if (_activeJob is { } job)
        {
            try
            {
                await job.Completion.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Waiting for the recording to finalize at shutdown timed out");
            }
        }
    }

    /// <summary>
    /// Rows left "recording" by a crash: keep whatever landed on disk. A surviving partial is
    /// finalized as Completed; nothing usable marks the row Failed. Never re-record — the
    /// broadcast moment has passed.
    /// </summary>
    private async Task ReconcileInterruptedAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _shutdownCts.Token); // let migrations settle

            var stuck = await _repo.GetByStatusAsync(DownloadStatus.Downloading, CancellationToken.None);
            foreach (var recording in stuck)
            {
                if (_activeJob?.Row.Id == recording.Id)
                {
                    continue; // started this session; not an orphan
                }

                var part = recording.FilePath + ".part.ts";
                if (File.Exists(part) && new FileInfo(part).Length >= 4096)
                {
                    var info = new FileInfo(part);
                    File.Move(part, recording.FilePath, overwrite: true);
                    var stopped = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                    var duration = Math.Max(0, stopped - recording.StartedUtc);
                    await _repo.UpdateStatusAsync(
                        recording.Id, DownloadStatus.Completed, null, stopped, duration, info.Length,
                        CancellationToken.None);
                    _logger.LogInformation(
                        "Recovered interrupted recording {Channel} ({Bytes} bytes)",
                        recording.ChannelName, info.Length);
                }
                else
                {
                    TryDelete(part);
                    await _repo.UpdateStatusAsync(
                        recording.Id, DownloadStatus.Failed, "Interrupted before anything was captured.",
                        _clock.UtcNow.ToUnixTimeSeconds(), null, 0, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciling interrupted recordings failed");
        }
    }

    // ---- Public API -------------------------------------------------------------------------

    /// <summary>Starts recording a channel on its own connection. One capture at a time.</summary>
    public async Task<RecordStartOutcome> StartRecordingAsync(
        Channel channel, string? programmeTitle, long profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (_activeJob is { } running)
        {
            return new RecordStartOutcome(RecordStartStatus.Busy, null, running.Row.ChannelName);
        }

        if (_playback.TryResolveLiveMedia(channel) is not { } media)
        {
            return new RecordStartOutcome(RecordStartStatus.Unresolvable, null, null);
        }

        var startedUtc = _clock.UtcNow;
        var recording = new Recording
        {
            ProfileId = profileId,
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            ProgrammeTitle = programmeTitle,
            LogoUrl = channel.LogoUrl,
            FilePath = BuildFilePath(profileId, channel.Name, startedUtc),
            Status = DownloadStatus.Downloading,
            StartedUtc = startedUtc.ToUnixTimeSeconds(),
        };
        recording.Id = await _repo.InsertAsync(recording, cancellationToken);

        var row = await OnUiAsync(() =>
        {
            var created = new RecordingRow(recording);
            Recordings.Insert(0, created);
            ActiveRecording = created;
            return created;
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var job = new ActiveJob(row, cts);
        _activeJob = job;

        // The capture outlives the start call on purpose; only its own CTS/shutdown stop it.
        job.Completion = Task.Run(() => RunJobAsync(job, media), CancellationToken.None);

        _messenger.Send(new RecordingStateChangedMessage(recording.Id, DownloadStatus.Downloading));
        return new RecordStartOutcome(RecordStartStatus.Started, row, null);
    }

    /// <summary>Stops the active capture and finalizes its file. No-op for other ids.</summary>
    public async Task StopRecordingAsync(long id)
    {
        if (_activeJob is { } job && job.Row.Id == id)
        {
            job.Cts.Cancel();
            try
            {
                await job.Completion;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Recording {Id} faulted while stopping", id);
            }
        }
    }

    /// <summary>Renames a recording's display title (blank clears back to the captured metadata).</summary>
    public async Task RenameAsync(long id, string? newTitle, CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();
        await _repo.UpdateTitleAsync(id, title, cancellationToken);
        OnUi(() =>
        {
            if (Recordings.FirstOrDefault(r => r.Id == id) is { } row)
            {
                row.ApplyTitle(title);
                _messenger.Send(new RecordingStateChangedMessage(id, row.Status));
            }
        });
    }

    /// <summary>Removes a recording: stops it if active, deletes its files and row.</summary>
    public async Task RemoveAsync(long id, CancellationToken cancellationToken)
    {
        await StopRecordingAsync(id);

        var row = await OnUiAsync(() => Recordings.FirstOrDefault(r => r.Id == id));
        await _repo.DeleteAsync(id, cancellationToken);
        if (row is not null)
        {
            TryDelete(row.FilePath);
            TryDelete(row.FilePath + ".part.ts");
            OnUi(() =>
            {
                Recordings.Remove(row);
                _messenger.Send(new RecordingStateChangedMessage(id, row.Status));
            });
        }
    }

    /// <summary>Loads a profile's recordings into <see cref="Recordings"/> for the page.</summary>
    public async Task EnsureLoadedAsync(long profileId, CancellationToken cancellationToken)
    {
        var rows = await _repo.GetAllAsync(profileId, cancellationToken);
        await OnUiAsync(() =>
        {
            foreach (var recording in rows)
            {
                if (Recordings.All(r => r.Id != recording.Id))
                {
                    Recordings.Add(new RecordingRow(recording));
                }
            }

            return true;
        });
    }

    /// <summary>Removes every recording of a profile (files, rows, folder). Stops an active one.</summary>
    public async Task DeleteProfileRecordingsAsync(long profileId, CancellationToken cancellationToken)
    {
        if (_activeJob is { } job && job.Row.Item.ProfileId == profileId)
        {
            await StopRecordingAsync(job.Row.Id);
        }

        var rows = await _repo.GetByProfileForCleanupAsync(profileId, cancellationToken);
        foreach (var recording in rows)
        {
            await _repo.DeleteAsync(recording.Id, cancellationToken);
            TryDelete(recording.FilePath);
            TryDelete(recording.FilePath + ".part.ts");
        }

        await OnUiAsync(() =>
        {
            foreach (var row in Recordings.Where(r => r.Item.ProfileId == profileId).ToList())
            {
                Recordings.Remove(row);
            }

            return true;
        });

        var folder = Path.Combine(AppPaths.RecordingsDir, profileId.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deleting recordings folder for profile {ProfileId} failed", profileId);
        }
    }

    /// <summary>Total bytes and file count under the recordings directory (Settings › Storage).</summary>
    public Task<(long TotalBytes, int FileCount)> GetStorageStatsAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var root = AppPaths.RecordingsDir;
                if (!Directory.Exists(root))
                {
                    return (0L, 0);
                }

                long bytes = 0;
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes += new FileInfo(file).Length;
                    count++;
                }

                return (bytes, count);
            },
            cancellationToken);

    // ---- Worker -----------------------------------------------------------------------------

    private async Task RunJobAsync(ActiveJob job, LiveMediaRequest media)
    {
        var row = job.Row;
        var partPath = row.FilePath + ".part.ts";
        try
        {
            var throttled = new ThrottledRecordingProgress(this, row);
            var result = await _recorder.RunAsync(media, partPath, row.FilePath, throttled, job.Cts.Token);

            var stopped = _clock.UtcNow.ToUnixTimeSeconds();
            await _repo.UpdateStatusAsync(
                row.Id, DownloadStatus.Completed, null, stopped, result.DurationSeconds, result.SizeBytes,
                CancellationToken.None);
            row.Item.StoppedUtc = stopped;
            OnUi(() =>
            {
                row.ApplyFinal(DownloadStatus.Completed, null, result.DurationSeconds, result.SizeBytes);
                _messenger.Send(new RecordingStateChangedMessage(row.Id, DownloadStatus.Completed));
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording failed for {Channel}", row.ChannelName);
            var stopped = _clock.UtcNow.ToUnixTimeSeconds();
            await _repo.UpdateStatusAsync(
                row.Id, DownloadStatus.Failed, Truncate(ex.Message), stopped, null, 0, CancellationToken.None);
            OnUi(() =>
            {
                row.ApplyFinal(DownloadStatus.Failed, Truncate(ex.Message), null, 0);
                _messenger.Send(new RecordingStateChangedMessage(row.Id, DownloadStatus.Failed));
            });
        }
        finally
        {
            _activeJob = null;
            job.Cts.Dispose();
            OnUi(() =>
            {
                if (ReferenceEquals(ActiveRecording, row))
                {
                    ActiveRecording = null;
                }
            });
        }
    }

    private static string BuildFilePath(long profileId, string channelName, DateTimeOffset startedUtc)
    {
        var stamp = startedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{DownloadService.SanitizeTitle(channelName)}-{stamp}.ts";
        return Path.Combine(
            AppPaths.RecordingsDir, profileId.ToString(CultureInfo.InvariantCulture), fileName);
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private Task<T> OnUiAsync<T>(Func<T> func) =>
        _dispatcher.CheckAccess() ? Task.FromResult(func()) : _dispatcher.InvokeAsync(func).Task;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    private sealed class ActiveJob
    {
        public ActiveJob(RecordingRow row, CancellationTokenSource cts)
        {
            Row = row;
            Cts = cts;
        }

        public RecordingRow Row { get; }

        public CancellationTokenSource Cts { get; }

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>Marshals recorder ticks to the row on the UI thread, at most once a second.</summary>
    private sealed class ThrottledRecordingProgress : IProgress<RecordingProgress>
    {
        private readonly RecordingService _service;
        private readonly RecordingRow _row;
        private long _lastElapsed = -1;

        public ThrottledRecordingProgress(RecordingService service, RecordingRow row)
        {
            _service = service;
            _row = row;
        }

        public void Report(RecordingProgress value)
        {
            // The recorder polls twice a second; only whole-second changes reach the UI.
            if (value.ElapsedSeconds == _lastElapsed)
            {
                return;
            }

            _lastElapsed = value.ElapsedSeconds;
            _service.OnUi(() => _row.ApplyProgress(value.ElapsedSeconds, value.SizeBytes));
        }
    }
}
