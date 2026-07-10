using System.Diagnostics;
using System.IO;
using System.Text;
using Lumen.App.Services;
using Lumen.App.Services.Downloads;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Downloads gate: enqueues a progressive movie download against the dev-server fixture, waits for
/// it to complete, then confirms the file landed and plays back offline from a <c>file://</c> MRL.
/// Exercises the real <see cref="DownloadService"/> + <see cref="ProgressiveDownloader"/> path and
/// the offline-playback wiring end-to-end.
/// </summary>
public static class E2eDownloadRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);

            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var xtream = profiles.FirstOrDefault(p => p.Kind == ProfileKind.Xtream && p.Name == "E2E Xtream")
                ?? profiles.First(p => p.Kind == ProfileKind.Xtream);
            await session.SwitchProfileAsync(xtream.Id, CancellationToken.None);

            var catalog = services.GetRequiredService<ICatalogRepository>();
            var movies = await catalog.GetVodItemsAsync(
                xtream.Id, ContentKind.Movie, null, null, VodSortOrder.Added, 10, 0, CancellationToken.None);
            report.AppendLine($"movies={movies.Count}");
            if (movies.Count == 0)
            {
                report.AppendLine("DOWNLOAD-RESULT=FAIL (no movies)");
                return 1;
            }

            var movie = movies[0];
            var downloads = services.GetRequiredService<DownloadService>();

            // Clean slate: remove any download of this fixture from a prior run.
            await downloads.DeleteProfileDownloadsAsync(xtream.Id, CancellationToken.None);

            var request = new DownloadRequest(
                ContentKind.Movie, movie.ProviderItemId, null, movie.ProviderItemId, movie.ContainerExtension,
                movie.StreamUrl, movie.Name, movie.PosterUrl, null, null, IsHls: false, xtream.Id);

            var row = await downloads.EnqueueAsync(request, CancellationToken.None);
            report.AppendLine($"enqueued id={row.Id} title=\"{row.Title}\"");

            // Idempotent enqueue: a second call returns the same row, not a duplicate.
            var again = await downloads.EnqueueAsync(request, CancellationToken.None);
            var idempotent = again.Id == row.Id;
            report.AppendLine($"idempotent={idempotent}");

            var progressed = await WaitForAsync(
                () => row.Status == DownloadStatus.Downloading || row.DownloadedBytes > 0,
                TimeSpan.FromSeconds(30));
            report.AppendLine($"started={progressed} status={row.Status}");

            var completed = await WaitForAsync(
                () => row.Status == DownloadStatus.Completed, TimeSpan.FromSeconds(120));
            report.AppendLine($"completed={completed} status={row.Status} bytes={row.DownloadedBytes}");
            if (!completed)
            {
                report.AppendLine($"error={row.Error}");
                report.AppendLine("DOWNLOAD-RESULT=FAIL (did not complete)");
                return 1;
            }

            var fileExists = File.Exists(row.FilePath);
            var fileSize = fileExists ? new FileInfo(row.FilePath).Length : 0;
            report.AppendLine($"file=\"{row.FilePath}\" exists={fileExists} size={fileSize}");

            // Play the downloaded file offline (file:// MRL, same ItemKey).
            var playback = services.GetRequiredService<PlaybackService>();
            playback.IsMuted = true;
            var url = new Uri(row.FilePath).AbsoluteUri;
            report.AppendLine($"offlineUrl={url}");
            await playback.PlayVodAsync(
                new VodPlayRequest(url, ContentKind.Movie, row.ItemKey, row.Title, row.PosterUrl, ResumeSeconds: 0),
                CancellationToken.None);
            var playing = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
            report.AppendLine($"offlinePlay state={playback.State} playing={playing}");
            playback.Stop();
            await WaitForAsync(() => playback.State == PlaybackState.Idle, TimeSpan.FromSeconds(5));

            // ---- HLS movie: recorded through the headless sout pipeline, then played offline ----
            var hlsMovie = movies.FirstOrDefault(m =>
                string.Equals(m.ContainerExtension, "m3u8", StringComparison.OrdinalIgnoreCase));
            report.AppendLine($"hlsMovie={hlsMovie?.Name ?? "(none)"}");
            var hlsCompleted = false;
            var hlsSize = 0L;
            var hlsPlaying = false;
            if (hlsMovie is not null)
            {
                var hlsRequest = new DownloadRequest(
                    ContentKind.Movie, hlsMovie.ProviderItemId, null, hlsMovie.ProviderItemId,
                    hlsMovie.ContainerExtension, hlsMovie.StreamUrl, hlsMovie.Name, hlsMovie.PosterUrl,
                    null, null, IsHls: true, xtream.Id);
                var hlsRow = await downloads.EnqueueAsync(hlsRequest, CancellationToken.None);
                report.AppendLine($"hlsEnqueued id={hlsRow.Id} title=\"{hlsRow.Title}\"");

                // The recording plays through the stream; allow real-time duration plus margin.
                await WaitForAsync(
                    () => hlsRow.Status is DownloadStatus.Completed or DownloadStatus.Failed,
                    TimeSpan.FromSeconds(150));
                hlsCompleted = hlsRow.Status == DownloadStatus.Completed;
                report.AppendLine($"hlsCompleted={hlsCompleted} status={hlsRow.Status} error={hlsRow.Error}");

                hlsSize = File.Exists(hlsRow.FilePath) ? new FileInfo(hlsRow.FilePath).Length : 0;
                report.AppendLine($"hlsFile=\"{hlsRow.FilePath}\" size={hlsSize}");

                if (hlsCompleted && hlsSize > 0)
                {
                    var hlsUrl = new Uri(hlsRow.FilePath).AbsoluteUri;
                    await playback.PlayVodAsync(
                        new VodPlayRequest(
                            hlsUrl, ContentKind.Movie, hlsRow.ItemKey, hlsRow.Title, hlsRow.PosterUrl,
                            ResumeSeconds: 0),
                        CancellationToken.None);
                    hlsPlaying = await WaitForAsync(
                        () => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
                    report.AppendLine($"hlsOfflinePlay state={playback.State} playing={hlsPlaying}");
                    playback.Stop();
                }
            }

            var pass = completed && idempotent && fileExists && fileSize > 1024 && playing
                && hlsCompleted && hlsSize > 10_000 && hlsPlaying;
            report.AppendLine(pass ? "DOWNLOAD-RESULT=PASS" : "DOWNLOAD-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E download run failed");
            report.AppendLine($"DOWNLOAD-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }
}
