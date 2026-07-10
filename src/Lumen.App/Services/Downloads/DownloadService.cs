using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.ViewModels;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Downloads;

/// <summary>The metadata needed to enqueue a download; built at the call site (a detail-page command).</summary>
public sealed record DownloadRequest(
    ContentKind Kind,
    string ItemKey,
    string? SeriesItemKey,
    string ProviderItemId,
    string? ContainerExtension,
    string? StreamUrl,
    string Title,
    string? PosterUrl,
    int? Season,
    int? EpisodeNumber,
    bool IsHls,
    long ProfileId);

/// <summary>
/// Owns the download queue for the whole app: one worker per job, capped concurrency per strategy,
/// SQLite-persisted state, and a live <see cref="ObservableCollection{T}"/> of <see cref="DownloadRow"/>
/// the UI binds to. Interrupted jobs resume on startup. The two strategies (progressive HTTP,
/// HLS recording) are transparent to callers.
/// </summary>
public sealed class DownloadService : IHostedService
{
    private readonly IDownloadRepository _repo;
    private readonly IProfileRepository _profiles;
    private readonly VodService _vodService;
    private readonly ProgressiveDownloader _progressiveDownloader;
    private readonly HlsRecorder _hlsRecorder;
    private readonly IMessenger _messenger;
    private readonly IClock _clock;
    private readonly ILogger<DownloadService> _logger;
    private readonly Dispatcher _dispatcher;

    private readonly ConcurrentDictionary<long, DownloadJob> _jobs = new();
    private readonly SemaphoreSlim _progressiveSlots = new(1, 1);
    private readonly SemaphoreSlim _hlsSlots = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    public DownloadService(
        IDownloadRepository repo,
        IProfileRepository profiles,
        VodService vodService,
        ProgressiveDownloader progressiveDownloader,
        HlsRecorder hlsRecorder,
        IMessenger messenger,
        IClock clock,
        ILogger<DownloadService> logger)
    {
        _repo = repo;
        _profiles = profiles;
        _vodService = vodService;
        _progressiveDownloader = progressiveDownloader;
        _hlsRecorder = hlsRecorder;
        _messenger = messenger;
        _clock = clock;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>Every download the service knows about (all profiles); the page filters by profile.</summary>
    public ObservableCollection<DownloadRow> Downloads { get; } = [];

    // ---- Lifecycle (IHostedService) ---------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ResumeInterruptedAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        var running = _jobs.Values.Select(j => j.Completion).ToArray();
        if (running.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Waiting for downloads to pause at shutdown timed out");
        }
    }

    private async Task ResumeInterruptedAsync()
    {
        try
        {
            // Let startup settle; the DB is migrated after host start, so don't race it.
            await Task.Delay(TimeSpan.FromSeconds(4), _shutdownCts.Token);

            var interrupted = await _repo.GetByStatusesAsync(
                [DownloadStatus.Queued, DownloadStatus.Downloading], CancellationToken.None);
            foreach (var item in interrupted)
            {
                // HLS recordings can't resume; drop any stale partial and re-record from scratch.
                if (item.IsHls)
                {
                    TryDelete(item.FilePath + ".part.ts");
                }

                var row = await AddOrGetRowAsync(item);
                StartJob(row);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resuming interrupted downloads failed");
        }
    }

    // ---- Public API -------------------------------------------------------------------------

    /// <summary>Enqueues a download (idempotent) and returns the live row for immediate button binding.</summary>
    public async Task<DownloadRow> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var item = new DownloadItem
        {
            ProfileId = request.ProfileId,
            Kind = request.Kind,
            ItemKey = request.ItemKey,
            SeriesItemKey = request.SeriesItemKey,
            ProviderItemId = request.ProviderItemId,
            ContainerExtension = request.ContainerExtension,
            StreamUrl = request.StreamUrl,
            Title = request.Title,
            PosterUrl = request.PosterUrl,
            Season = request.Season,
            EpisodeNumber = request.EpisodeNumber,
            IsHls = request.IsHls,
            FilePath = BuildFilePath(request),
            Status = DownloadStatus.Queued,
            CreatedUtc = now,
        };

        var id = await _repo.InsertAsync(item, cancellationToken);
        var stored = await _repo.GetByItemKeyAsync(request.ProfileId, request.Kind, request.ItemKey, cancellationToken)
            ?? item;
        stored.Id = id;

        var row = await AddOrGetRowAsync(stored);
        if (stored.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
        {
            StartJob(row);
        }

        return row;
    }

    /// <summary>Loads a profile's downloads (all statuses) into <see cref="Downloads"/> for the page.</summary>
    public async Task EnsureLoadedAsync(long profileId, CancellationToken cancellationToken)
    {
        var rows = await _repo.GetAllAsync(profileId, cancellationToken);
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var item in rows)
            {
                AddOrGetRowOnUi(item);
            }
        }).Task;
    }

    /// <summary>Finds the live row for an item (loading it from the DB if needed); null if not downloaded.</summary>
    public async Task<DownloadRow?> FindByItemKeyAsync(
        long profileId, ContentKind kind, string itemKey, CancellationToken cancellationToken)
    {
        var inMemory = await _dispatcher.InvokeAsync(() =>
            Downloads.FirstOrDefault(r => r.Item.ProfileId == profileId && r.Kind == kind && r.ItemKey == itemKey)).Task;
        if (inMemory is not null)
        {
            return inMemory;
        }

        var stored = await _repo.GetByItemKeyAsync(profileId, kind, itemKey, cancellationToken);
        return stored is null ? null : await AddOrGetRowAsync(stored);
    }

    public void Pause(long id)
    {
        if (_jobs.TryGetValue(id, out var job) && !job.Row.IsHls)
        {
            job.PauseRequested = true;
            job.Cts.Cancel();
        }
    }

    public void Resume(long id) => RestartById(id);

    public void Retry(long id) => RestartById(id);

    /// <summary>Cancels the job (if running), deletes its files and row, and notifies listeners.</summary>
    public async Task RemoveAsync(long id, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.RemoveRequested = true;
            job.Cts.Cancel();
            try
            {
                await job.Completion;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Job {Id} faulted while being removed", id);
            }
        }

        var row = await _dispatcher.InvokeAsync(() => Downloads.FirstOrDefault(r => r.Id == id)).Task;
        await _repo.DeleteAsync(id, cancellationToken);
        if (row is not null)
        {
            DeleteFiles(row.Item);
            OnUi(() =>
            {
                Downloads.Remove(row);
                _messenger.Send(new DownloadRemovedMessage(id, row.ItemKey));
            });
        }
    }

    /// <summary>Removes every download for a profile: cancels jobs, deletes rows, files, and the folder.</summary>
    public async Task DeleteProfileDownloadsAsync(long profileId, CancellationToken cancellationToken)
    {
        foreach (var job in _jobs.Values.Where(j => j.Row.Item.ProfileId == profileId).ToList())
        {
            job.RemoveRequested = true;
            job.Cts.Cancel();
            try
            {
                await job.Completion;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Job faulted while clearing profile {ProfileId}", profileId);
            }
        }

        var rows = await _repo.GetByProfileForCleanupAsync(profileId, cancellationToken);
        foreach (var item in rows)
        {
            await _repo.DeleteAsync(item.Id, cancellationToken);
            DeleteFiles(item);
        }

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var row in Downloads.Where(r => r.Item.ProfileId == profileId).ToList())
            {
                Downloads.Remove(row);
                _messenger.Send(new DownloadRemovedMessage(row.Id, row.ItemKey));
            }
        }).Task;

        DeleteProfileFolder(profileId);
    }

    /// <summary>Total bytes and file count under the downloads directory (Settings › Storage).</summary>
    public Task<(long TotalBytes, int FileCount)> GetStorageStatsAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var root = AppPaths.DownloadsDir;
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

    private void RestartById(long id)
    {
        var row = Downloads.FirstOrDefault(r => r.Id == id);
        if (row is not null && !row.IsCompleted)
        {
            StartJob(row);
        }
    }

    private void StartJob(DownloadRow row)
    {
        if (row.IsCompleted)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var job = new DownloadJob(row, cts);
        if (!_jobs.TryAdd(row.Id, job))
        {
            cts.Dispose();
            return; // already running
        }

        job.Completion = Task.Run(() => RunJobAsync(job));
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var row = job.Row;
        var token = job.Cts.Token;
        var slot = row.IsHls ? _hlsSlots : _progressiveSlots;
        var handoffToHls = false;

        try
        {
            await UpdateStatusAsync(row, DownloadStatus.Queued, null, null);
            await slot.WaitAsync(token);
            try
            {
                var context = await BuildContextAsync(row, token);
                if (context is null)
                {
                    await UpdateStatusAsync(row, DownloadStatus.Failed, "Could not resolve the stream URL.", null);
                    return;
                }

                await UpdateStatusAsync(row, DownloadStatus.Downloading, null, null);
                IVodDownloader downloader = row.IsHls ? _hlsRecorder : _progressiveDownloader;
                var progress = new ThrottledProgress(this, row);
                try
                {
                    await downloader.RunAsync(context, progress, token);
                }
                catch (HlsHandoffException)
                {
                    _logger.LogInformation("{Title} is an HLS stream; re-routing to the recorder", row.Title);
                    TryDelete(context.PartPath);
                    handoffToHls = true;
                    return;
                }

                await CompleteAsync(row);
            }
            finally
            {
                slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (!job.RemoveRequested && job.PauseRequested)
            {
                await UpdateStatusAsync(row, DownloadStatus.Paused, null, null);
            }

            // Otherwise: user removal (handled by RemoveAsync) or app shutdown — leave the DB status
            // as Downloading so the startup scan resumes it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed for {Title}", row.Title);
            await UpdateStatusAsync(row, DownloadStatus.Failed, Truncate(ex.Message), null);
        }
        finally
        {
            _jobs.TryRemove(row.Id, out _);
            job.Cts.Dispose();

            if (handoffToHls)
            {
                _ = RestartAsHlsAsync(row);
            }
        }
    }

    private async Task RestartAsHlsAsync(DownloadRow row)
    {
        try
        {
            row.Item.IsHls = true;
            await _repo.UpdateIsHlsAsync(row.Id, true, CancellationToken.None);
            StartJob(row);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restarting {Title} as an HLS recording failed", row.Title);
            await UpdateStatusAsync(row, DownloadStatus.Failed, Truncate(ex.Message), null);
        }
    }

    private async Task<DownloadContext?> BuildContextAsync(DownloadRow row, CancellationToken cancellationToken)
    {
        var item = row.Item;
        var profile = await _profiles.GetAsync(item.ProfileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var url = item.Kind == ContentKind.Series
            ? _vodService.ResolveEpisodeUrl(profile, item.ProviderItemId, item.ContainerExtension)
            : _vodService.ResolveMovieUrl(profile, item.ProviderItemId, item.ContainerExtension, item.StreamUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var userAgent = string.IsNullOrWhiteSpace(profile.StreamUserAgent)
            ? Profile.DefaultStreamUserAgent
            : profile.StreamUserAgent;
        var partPath = item.FilePath + (row.IsHls ? ".part.ts" : ".part");
        return new DownloadContext(url, userAgent, partPath, item.FilePath, row.IsHls);
    }

    private async Task CompleteAsync(DownloadRow row)
    {
        var now = _clock.UtcNow.ToUnixTimeSeconds();
        await _repo.UpdateProgressAsync(row.Id, row.DownloadedBytes, row.TotalBytes, 1000, CancellationToken.None);
        OnUi(() => row.ApplyProgress(new DownloadProgress(row.DownloadedBytes, row.TotalBytes, 1000)));
        await UpdateStatusAsync(row, DownloadStatus.Completed, null, now);
        OnUi(() => _messenger.Send(new DownloadCompletedMessage(row.Item)));
        _logger.LogInformation("Download completed: {Title}", row.Title);
    }

    private async Task UpdateStatusAsync(DownloadRow row, DownloadStatus status, string? error, long? completedUtc)
    {
        await _repo.UpdateStatusAsync(row.Id, status, error, completedUtc, CancellationToken.None);
        OnUi(() =>
        {
            row.Status = status;
            row.Error = error;
            row.Item.Status = status;
            row.Item.Error = error;
            if (completedUtc is not null)
            {
                row.Item.CompletedUtc = completedUtc;
            }

            _messenger.Send(new DownloadStateChangedMessage(row.Id, row.ItemKey, status));
        });
    }

    // ---- Collection helpers (UI thread) -----------------------------------------------------

    private Task<DownloadRow> AddOrGetRowAsync(DownloadItem item) =>
        _dispatcher.CheckAccess()
            ? Task.FromResult(AddOrGetRowOnUi(item))
            : _dispatcher.InvokeAsync(() => AddOrGetRowOnUi(item)).Task;

    private DownloadRow AddOrGetRowOnUi(DownloadItem item)
    {
        var existing = Downloads.FirstOrDefault(r => r.Id == item.Id);
        if (existing is not null)
        {
            existing.Sync(item);
            return existing;
        }

        var row = new DownloadRow(item);
        Downloads.Add(row);
        return row;
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

    // ---- File naming / cleanup --------------------------------------------------------------

    private static string BuildFilePath(DownloadRequest request)
    {
        var ext = request.IsHls ? "ts" : NormalizeExtension(request.ContainerExtension, request.StreamUrl);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ItemKey)))
            .ToLowerInvariant()[..8];
        var kindLetter = request.Kind == ContentKind.Series ? "e" : "m";
        var fileName = $"{SanitizeTitle(request.Title)}-{kindLetter}-{hash}.{ext}";
        return Path.Combine(
            AppPaths.DownloadsDir, request.ProfileId.ToString(CultureInfo.InvariantCulture), fileName);
    }

    private static string NormalizeExtension(string? container, string? streamUrl)
    {
        var ext = container?.Trim().TrimStart('.');
        if (string.IsNullOrEmpty(ext) && !string.IsNullOrWhiteSpace(streamUrl))
        {
            var path = streamUrl.Split('?')[0];
            var dot = path.LastIndexOf('.');
            var slash = path.LastIndexOf('/');
            if (dot > slash && dot >= 0 && dot < path.Length - 1)
            {
                ext = path[(dot + 1)..];
            }
        }

        return string.IsNullOrEmpty(ext) ? "mp4" : ext;
    }

    /// <summary>Shared with the live recording service — both write sout-safe file names.</summary>
    internal static string SanitizeTitle(string title)
    {
        // Drop OS-invalid chars plus single quotes/braces so the path is safe inside the LibVLC
        // sout config chain (which wraps the destination in single quotes).
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(
            title.Where(c => !invalid.Contains(c) && c is not ('\'' or '{' or '}')).ToArray()).Trim();
        if (clean.Length > 80)
        {
            clean = clean[..80].Trim();
        }

        return clean.Length == 0 ? "download" : clean;
    }

    private static void DeleteFiles(DownloadItem item)
    {
        TryDelete(item.FilePath);
        TryDelete(item.FilePath + ".part");
        TryDelete(item.FilePath + ".part.ts");
    }

    private void DeleteProfileFolder(long profileId)
    {
        var folder = Path.Combine(AppPaths.DownloadsDir, profileId.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deleting download folder for profile {ProfileId} failed", profileId);
        }
    }

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

    // ---- Nested types -----------------------------------------------------------------------

    private sealed class DownloadJob
    {
        public DownloadJob(DownloadRow row, CancellationTokenSource cts)
        {
            Row = row;
            Cts = cts;
        }

        public DownloadRow Row { get; }

        public CancellationTokenSource Cts { get; }

        public bool PauseRequested { get; set; }

        public bool RemoveRequested { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>Throttles progress: the observable row updates ≤4×/s, the DB ≤ every 2 s.</summary>
    private sealed class ThrottledProgress : IProgress<DownloadProgress>
    {
        private readonly DownloadService _service;
        private readonly DownloadRow _row;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastUiMs = -1000;
        private long _lastDbMs = -10000;

        public ThrottledProgress(DownloadService service, DownloadRow row)
        {
            _service = service;
            _row = row;
        }

        public void Report(DownloadProgress value)
        {
            var now = _stopwatch.ElapsedMilliseconds;
            if (now - _lastUiMs >= 250)
            {
                _lastUiMs = now;
                _service.OnUi(() => _row.ApplyProgress(value));
            }

            if (now - _lastDbMs >= 2000)
            {
                _lastDbMs = now;
                _ = _service._repo.UpdateProgressAsync(
                    _row.Id, value.DownloadedBytes, value.TotalBytes, value.ProgressPermille, CancellationToken.None);
            }
        }
    }
}
