using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Lumen.Core.Updates;
using Lumen.Providers;
using Lumen.Providers.Updates;
using Microsoft.Win32;
using Serilog;

namespace Lumen.App.Services;

/// <summary>Where the updater currently sits in its check → download → install lifecycle.</summary>
public enum UpdateStatus
{
    /// <summary>No newer version known (up to date, or the check hasn't found one).</summary>
    Idle,

    /// <summary>A check is in flight.</summary>
    Checking,

    /// <summary>A newer version exists but is not yet downloaded (or cannot be auto-installed).</summary>
    Available,

    /// <summary>The installer for the newer version is downloading.</summary>
    Downloading,

    /// <summary>The installer is downloaded and verified, ready to run.</summary>
    ReadyToInstall,

    /// <summary>The last download attempt failed; it will be retried on the next check.</summary>
    Failed,
}

/// <summary>Outcome of attempting to launch the downloaded installer.</summary>
public enum InstallLaunchResult
{
    /// <summary>The installer process started; the app should now shut down.</summary>
    Launched,

    /// <summary>The user dismissed the UAC elevation prompt.</summary>
    Declined,

    /// <summary>Launching the installer failed for another reason.</summary>
    Failed,

    /// <summary>No verified installer is available to launch.</summary>
    NotReady,
}

/// <summary>An immutable view of the updater's state for the UI to bind against.</summary>
public sealed record UpdateSnapshot(
    UpdateStatus Status,
    string CurrentVersion,
    string? AvailableVersion,
    string? ReleaseNotes,
    Uri? ReleaseUrl,
    bool CanAutoUpdate,
    double Percent,
    long BytesReceived,
    long TotalBytes,
    double BytesPerSecond);

/// <summary>
/// Orchestrates in-app updates from GitHub Releases: compares the running build against the newest
/// release, auto-downloads the installer (for installed builds) with progress, verifies it, and
/// launches it silently. Mirrors the settings/idiom of <see cref="SupportService"/>. Portable builds
/// are detected and steered to the release page instead of an in-place install.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Global setting: "false" disables automatic checks and downloads (default on).</summary>
    public const string AutoCheckEnabledKey = "update_auto_check_enabled";

    /// <summary>Global setting: hours between background re-checks (default 24).</summary>
    public const string FrequencyHoursKey = "update_check_frequency_hours";

    /// <summary>Global setting: "true" to also offer pre-releases (default off).</summary>
    public const string IncludePrereleaseKey = "update_include_prerelease";

    /// <summary>Global setting: a release tag the user chose to skip.</summary>
    public const string SkippedVersionKey = "update_skipped_version";

    /// <summary>Global setting: unix seconds of the last successful check.</summary>
    public const string LastCheckKey = "update_last_check_utc";

    /// <summary>Default and fallback cadence for background checks.</summary>
    public const int DefaultFrequencyHours = 24;

    // Inno Setup writes a per-machine uninstall key named "{AppId}_is1"; its presence marks an
    // installed build (portable extractions have none). The AppId matches build/Lumen.iss.
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9B3F5B1E-3C1D-49D2-9C0E-4C8DFF000001}_is1";

    private readonly IGitHubReleaseClient _github;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsRepository _settings;
    private readonly IClock _clock;

    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ReleaseVersion _current;
    private readonly bool _isInstalledBuild;

    // Guarded by _stateLock.
    private UpdateStatus _status = UpdateStatus.Idle;
    private GitHubRelease? _release;
    private string? _availableVersionText;
    private string? _releaseNotes;
    private Uri? _releaseUrl;
    private string? _installerPath;
    private double _percent;
    private long _bytesReceived;
    private long _totalBytes;
    private double _bytesPerSecond;

    public UpdateService(
        IGitHubReleaseClient github,
        IHttpClientFactory httpClientFactory,
        ISettingsRepository settings,
        IClock clock)
    {
        _github = github;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _clock = clock;

        var version = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _current = ReleaseVersion.TryParse(version, out var parsed) ? parsed : ReleaseVersion.Parse("0.0.0");
        _isInstalledBuild = DetectInstalledBuild();
    }

    /// <summary>Raised whenever the snapshot changes. Handlers must marshal to the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The current running version, formatted as <c>major.minor.patch</c>.</summary>
    public string CurrentVersion => _current.ToString();

    /// <summary>True for an installer-based build that can update itself in place.</summary>
    public bool IsInstalledBuild => _isInstalledBuild;

    /// <summary>A consistent snapshot of the updater's current state.</summary>
    public UpdateSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                var canAutoUpdate = _isInstalledBuild && _release?.InstallerAsset is not null;
                return new UpdateSnapshot(
                    _status,
                    _current.ToString(),
                    _availableVersionText,
                    _releaseNotes,
                    _releaseUrl,
                    canAutoUpdate,
                    _percent,
                    _bytesReceived,
                    _totalBytes,
                    _bytesPerSecond);
            }
        }
    }

    /// <summary>True unless the user has turned automatic checks off.</summary>
    public async Task<bool> IsAutoCheckEnabledAsync(CancellationToken cancellationToken) =>
        await _settings.GetAsync(0, AutoCheckEnabledKey, cancellationToken).ConfigureAwait(false) != "false";

    /// <summary>Configured hours between background checks (clamped, defaulted).</summary>
    public async Task<int> GetFrequencyHoursAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(0, FrequencyHoursKey, cancellationToken).ConfigureAwait(false);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? hours
            : DefaultFrequencyHours;
    }

    /// <summary>Unix seconds of the last successful check, or null if never checked.</summary>
    public async Task<long?> GetLastCheckUtcAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(0, LastCheckKey, cancellationToken).ConfigureAwait(false);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) ? unix : null;
    }

    /// <summary>
    /// Checks GitHub for a newer release. Automatic checks (<paramref name="manual"/> false) respect
    /// the enabled toggle and the skipped-version setting; manual checks always run and surface even a
    /// skipped version. On an installed build with an installer asset, a newer version auto-downloads.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!manual && !await IsAutoCheckEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        GitHubRelease? toDownload = null;
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_status is UpdateStatus.Downloading)
                {
                    return; // never interrupt an in-flight download
                }
            }

            SetStatus(_release is null ? UpdateStatus.Checking : _status);
            var includePrerelease =
                await _settings.GetAsync(0, IncludePrereleaseKey, cancellationToken).ConfigureAwait(false) == "true";
            var release = await _github.GetLatestReleaseAsync(includePrerelease, cancellationToken).ConfigureAwait(false);
            await _settings.SetAsync(
                    0,
                    LastCheckKey,
                    _clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    cancellationToken)
                .ConfigureAwait(false);

            if (release is null || !ReleaseVersion.TryParse(release.TagName, out var latest))
            {
                RevertCheckingStatus();
                return;
            }

            if (latest <= _current)
            {
                ClearAvailable();
                return;
            }

            if (!manual)
            {
                var skipped = await _settings.GetAsync(0, SkippedVersionKey, cancellationToken).ConfigureAwait(false);
                if (string.Equals(skipped, release.TagName, StringComparison.OrdinalIgnoreCase))
                {
                    ClearAvailable();
                    return;
                }
            }

            var canAutoUpdate = _isInstalledBuild && release.InstallerAsset is not null;
            lock (_stateLock)
            {
                _release = release;
                _availableVersionText = latest.ToString();
                _releaseNotes = release.Body;
                _releaseUrl = release.HtmlUrl;

                // A verified installer from an earlier session may already be on disk.
                if (canAutoUpdate && HasVerifiedInstaller(release.InstallerAsset!))
                {
                    _status = UpdateStatus.ReadyToInstall;
                }
                else if (_status is not UpdateStatus.ReadyToInstall)
                {
                    _status = UpdateStatus.Available;
                }
            }

            RaiseChanged();

            if (canAutoUpdate)
            {
                lock (_stateLock)
                {
                    if (_status is not UpdateStatus.ReadyToInstall)
                    {
                        toDownload = release;
                    }
                }
            }
        }
        finally
        {
            _checkGate.Release();
        }

        if (toDownload is not null)
        {
            await DownloadAsync(toDownload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DownloadAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        if (release.InstallerAsset is not { } asset)
        {
            return;
        }

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updatesDir = Path.Combine(AppPaths.DataRoot, "updates");
            Directory.CreateDirectory(updatesDir);
            var finalPath = Path.Combine(updatesDir, asset.Name);
            var partPath = finalPath + ".part";

            if (HasVerifiedInstaller(asset))
            {
                MarkReady(finalPath);
                return;
            }

            PurgeStaleFiles(updatesDir, asset.Name);
            SetStatus(UpdateStatus.Downloading);

            var http = _httpClientFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
            using var response = await http
                .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReportMs = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    if (elapsedMs - lastReportMs >= 250 || received == total)
                    {
                        lastReportMs = elapsedMs;
                        ReportProgress(received, total, stopwatch.Elapsed.TotalSeconds);
                    }
                }
            }

            var actualSize = new FileInfo(partPath).Length;
            if (total > 0 && actualSize != total)
            {
                throw new IOException($"Downloaded size {actualSize} did not match the expected {total}.");
            }

            if (!await VerifyChecksumAsync(partPath, asset.Name, release, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The downloaded installer failed checksum verification.");
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partPath, finalPath);
            MarkReady(finalPath);
            Log.Information("Downloaded update installer {Name}", asset.Name);
        }
        catch (OperationCanceledException)
        {
            SafeDeletePart(release.InstallerAsset, ".part");
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download failed");
            SafeDeletePart(release.InstallerAsset, ".part");
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>
    /// Launches the downloaded installer silently (Inno <c>/SILENT</c>: no wizard pages, but a visible
    /// progress window), then the caller should shut the app down so the files can be replaced. Shows
    /// one UAC prompt; the installer relaunches the app when it finishes.
    /// </summary>
    public InstallLaunchResult TryStartInstaller()
    {
        string? installer;
        lock (_stateLock)
        {
            installer = _installerPath;
        }

        if (installer is null || !File.Exists(installer))
        {
            return InstallLaunchResult.NotReady;
        }

        try
        {
            var startInfo = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS",
            };
            Process.Start(startInfo);
            Log.Information("Launched update installer {Path}", installer);
            return InstallLaunchResult.Launched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the elevation prompt. Keep the download for a retry.
            Log.Information("Update install postponed: elevation was declined");
            return InstallLaunchResult.Declined;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to launch the update installer");
            return InstallLaunchResult.Failed;
        }
    }

    /// <summary>Suppresses further prompts for the currently available version and hides the indicator.</summary>
    public async Task SkipCurrentVersionAsync(CancellationToken cancellationToken)
    {
        string? tag;
        lock (_stateLock)
        {
            tag = _release?.TagName;
        }

        if (tag is not null)
        {
            await _settings.SetAsync(0, SkippedVersionKey, tag, cancellationToken).ConfigureAwait(false);
        }

        ClearAvailable();
    }

    /// <summary>Opens the release page (or the releases index) in the default browser.</summary>
    public void OpenReleasePage()
    {
        Uri? url;
        lock (_stateLock)
        {
            url = _releaseUrl;
        }

        var target = url?.AbsoluteUri
            ?? $"https://github.com/{GitHubReleaseClient.RepositoryOwner}/{GitHubReleaseClient.RepositoryName}/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open the release page");
        }
    }

    private bool HasVerifiedInstaller(GitHubReleaseAsset asset)
    {
        var path = Path.Combine(AppPaths.DataRoot, "updates", asset.Name);
        return File.Exists(path) && (asset.Size <= 0 || new FileInfo(path).Length == asset.Size);
    }

    private async Task<bool> VerifyChecksumAsync(
        string filePath, string assetName, GitHubRelease release, CancellationToken cancellationToken)
    {
        if (release.ChecksumsAsset is not { } checksums)
        {
            // Older releases ship no manifest; TLS + the size check are the trust anchor.
            Log.Debug("No SHA256SUMS published for {Tag}; skipping hash verification", release.TagName);
            return true;
        }

        try
        {
            var http = _httpClientFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
            var manifest = await http.GetStringAsync(checksums.DownloadUrl, cancellationToken).ConfigureAwait(false);
            var expected = FindChecksum(manifest, assetName);
            if (expected is null)
            {
                Log.Warning("SHA256SUMS did not list {Asset}; proceeding on size check only", assetName);
                return true;
            }

            await using var stream = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash);
            var matches = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                Log.Warning("Checksum mismatch for {Asset}: expected {Expected}, got {Actual}", assetName, expected, actual);
            }

            return matches;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A hiccup fetching the tiny manifest shouldn't block an otherwise-valid update.
            Log.Warning(ex, "Could not fetch SHA256SUMS; proceeding on size check only");
            return true;
        }
    }

    // Manifest lines are "<hex-hash>  <filename>" (sha256sum format); match on the trailing name.
    private static string? FindChecksum(string manifest, string assetName)
    {
        foreach (var line in manifest.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var hash = trimmed[..separator];
            var name = trimmed[(separator + 1)..].TrimStart('*', ' ');
            if (name.EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }

    private static void PurgeStaleFiles(string updatesDir, string keepName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(updatesDir))
            {
                if (!string.Equals(Path.GetFileName(file), keepName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not purge stale update files");
        }
    }

    private void SafeDeletePart(GitHubReleaseAsset? asset, string suffix)
    {
        if (asset is null)
        {
            return;
        }

        try
        {
            var part = Path.Combine(AppPaths.DataRoot, "updates", asset.Name + suffix);
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not delete partial download");
        }
    }

    private static bool DetectInstalledBuild()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(UninstallKeyPath);
                if (key is not null)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Registry probe for install state failed");
            }
        }

        // Fallback heuristic: an installed build lives under Program Files.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return !string.IsNullOrEmpty(programFiles)
            && AppContext.BaseDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(UpdateStatus status)
    {
        lock (_stateLock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        RaiseChanged();
    }

    private void RevertCheckingStatus()
    {
        lock (_stateLock)
        {
            if (_status is UpdateStatus.Checking)
            {
                _status = _release is null ? UpdateStatus.Idle : UpdateStatus.Available;
            }
        }

        RaiseChanged();
    }

    private void ClearAvailable()
    {
        lock (_stateLock)
        {
            _release = null;
            _availableVersionText = null;
            _releaseNotes = null;
            _releaseUrl = null;
            _installerPath = null;
            _percent = 0;
            _bytesReceived = 0;
            _totalBytes = 0;
            _bytesPerSecond = 0;
            _status = UpdateStatus.Idle;
        }

        RaiseChanged();
    }

    private void MarkReady(string installerPath)
    {
        lock (_stateLock)
        {
            _installerPath = installerPath;
            _percent = 100;
            _status = UpdateStatus.ReadyToInstall;
        }

        RaiseChanged();
    }

    private void ReportProgress(long received, long total, double elapsedSeconds)
    {
        lock (_stateLock)
        {
            _bytesReceived = received;
            _totalBytes = total;
            _percent = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : 0;
            _bytesPerSecond = elapsedSeconds > 0 ? received / elapsedSeconds : 0;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
