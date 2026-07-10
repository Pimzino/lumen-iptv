using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Lumen.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Downloads;

/// <summary>
/// Downloads a progressive (direct-file) VOD stream to disk over HTTP, resuming a partial file via
/// a Range request. Mirrors <see cref="Lumen.Data.ImageDiskCache"/>'s temp-then-atomic-move
/// discipline but streams the body so multi-GB movies never buffer in memory.
/// </summary>
public sealed class ProgressiveDownloader : IVodDownloader
{
    private const int BufferSize = 81920;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProgressiveDownloader> _logger;

    public ProgressiveDownloader(IHttpClientFactory httpClientFactory, ILogger<ProgressiveDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task RunAsync(
        DownloadContext context, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        Directory.CreateDirectory(Path.GetDirectoryName(context.FinalPath)!);
        var client = _httpClientFactory.CreateClient("downloads");

        var existing = File.Exists(context.PartPath) ? new FileInfo(context.PartPath).Length : 0L;
        var response = await SendAsync(client, context, existing, cancellationToken);
        try
        {
            // A provider may answer a nominally-progressive URL with an HLS playlist — hand off.
            if (IsHlsResponse(response))
            {
                throw new HlsHandoffException($"HLS playlist returned for {context.FinalPath}");
            }

            // Our partial is stale/complete for the server's view: drop it and refetch from scratch.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                response.Dispose();
                TryDelete(context.PartPath);
                existing = 0;
                response = await SendAsync(client, context, existing, cancellationToken);
            }

            response.EnsureSuccessStatusCode();

            var plan = DownloadResume.Decide(
                existing, (int)response.StatusCode, response.Content.Headers.ContentLength);
            var appending = plan.Mode == ResumeMode.Append;
            var downloaded = appending ? existing : 0L;
            var total = plan.TotalBytes;

            await using (var file = new FileStream(
                context.PartPath, appending ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (body)
                {
                    var buffer = new byte[BufferSize];
                    progress.Report(new DownloadProgress(downloaded, total, Permille(downloaded, total)));

                    int read;
                    while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        downloaded += read;
                        progress.Report(new DownloadProgress(downloaded, total, Permille(downloaded, total)));
                    }

                    await file.FlushAsync(cancellationToken);
                }
            }

            File.Move(context.PartPath, context.FinalPath, overwrite: true);
            progress.Report(new DownloadProgress(downloaded, total ?? downloaded, 1000));
            _logger.LogInformation("Progressive download complete: {Path}", context.FinalPath);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, DownloadContext context, long existing, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Url);
        if (!string.IsNullOrWhiteSpace(context.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        }

        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>True when the response is an HLS playlist rather than a downloadable media file.</summary>
    private static bool IsHlsResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is not null && mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    private static int Permille(long downloaded, long? total) =>
        total is { } t && t > 0 ? (int)Math.Clamp(downloaded * 1000 / t, 0, 1000) : 0;

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
}
