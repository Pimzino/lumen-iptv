using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Lumen.Core;
using Lumen.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Lumen.Data;

/// <summary>
/// Disk cache for logos and posters keyed by URL hash. Concurrent requests for the same
/// URL share one download; failures are negatively cached briefly; total size is kept
/// under a cap by evicting least-recently-used files. Hosts that fail at the connection
/// level are marked down for a few minutes so a dead image server costs one connect
/// timeout, not one per poster.
/// </summary>
public sealed class ImageDiskCache : IImageCache
{
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HostFailureCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageDiskCache> _logger;
    private readonly string _root;
    private readonly string _httpClientName;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _downHosts = new(StringComparer.OrdinalIgnoreCase);
    private int _writesSinceSweep;
    private int _sweepRunning;

    public ImageDiskCache(IHttpClientFactory httpClientFactory, ILogger<ImageDiskCache> logger)
        : this(httpClientFactory, logger, AppPaths.ImageCacheDir, "images", maxBytes: 512L * 1024 * 1024)
    {
    }

    public ImageDiskCache(
        IHttpClientFactory httpClientFactory,
        ILogger<ImageDiskCache> logger,
        string rootDirectory,
        string httpClientName,
        long maxBytes)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _root = rootDirectory;
        _httpClientName = httpClientName;
        _maxBytes = maxBytes;
    }

    public async Task<string?> GetLocalPathAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = PathFor(url);
        if (File.Exists(path))
        {
            TouchQuietly(path);
            return path;
        }

        if (_recentFailures.TryGetValue(url, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < FailureCacheDuration)
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            _downHosts.TryGetValue(parsed.Authority, out var hostFailedAt) &&
            DateTimeOffset.UtcNow - hostFailedAt < HostFailureCacheDuration)
        {
            return null;
        }

        var download = _inflight.GetOrAdd(url, u => DownloadAsync(u, path));
        try
        {
            return await download.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (download.IsCompleted)
            {
                _inflight.TryRemove(url, out _);
            }
        }
    }

    public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(_root))
        {
            return new CacheStats(0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += new FileInfo(file).Length;
            count++;
        }

        return new CacheStats(bytes, count);
    }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(file);
        }
    }, cancellationToken);

    private async Task<string?> DownloadAsync(string url, string path)
    {
        try
        {
            var uri = new Uri(url);
            var client = _httpClientFactory.CreateClient(_httpClientName);
            var bytes = await client.GetByteArrayAsync(uri).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("Empty response.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            _downHosts.TryRemove(uri.Authority, out _);

            if (Interlocked.Increment(ref _writesSinceSweep) % 50 == 0)
            {
                _ = Task.Run(SweepIfOverBudget);
            }

            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or InvalidOperationException or UriFormatException)
        {
            _recentFailures[url] = DateTimeOffset.UtcNow;
            if (IsHostLevelFailure(ex) && Uri.TryCreate(url, UriKind.Absolute, out var failedUri))
            {
                _downHosts[failedUri.Authority] = DateTimeOffset.UtcNow;
            }

            _logger.LogDebug(ex, "Image download failed for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Failures that condemn the whole host rather than one image. Downloads run detached
    /// (no caller token), so a TaskCanceledException here is always the client timeout.
    /// </summary>
    private static bool IsHostLevelFailure(Exception ex) => ex switch
    {
        HttpRequestException
        {
            HttpRequestError: HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError,
        } => true,
        TaskCanceledException => true,
        _ => false,
    };

    private void SweepIfOverBudget()
    {
        if (Interlocked.Exchange(ref _sweepRunning, 1) == 1)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .ToList();
            var total = files.Sum(LengthOrZero);
            if (total <= _maxBytes)
            {
                return;
            }

            var target = (long)(_maxBytes * 0.9);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target)
                {
                    break;
                }

                total -= LengthOrZero(file);
                TryDelete(file.FullName);
            }

            _logger.LogInformation("Image cache swept down to {Bytes} bytes", total);
        }
        finally
        {
            Interlocked.Exchange(ref _sweepRunning, 0);
        }
    }

    /// <summary>In-flight .tmp files can vanish between enumeration and stat.</summary>
    private static long LengthOrZero(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_root, hash[..2], hash + ".img");
    }

    private static void TouchQuietly(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
