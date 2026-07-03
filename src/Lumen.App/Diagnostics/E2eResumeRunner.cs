using System.Diagnostics;
using System.IO;
using System.Text;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Phase-6 gate: plays a movie, lets it run, "quits" (stops) partway through, then confirms a
/// resume position was written to watch history and that a fresh play request seeks back to it.
/// Simulates the manual "play, quit at 10 min, relaunch, resume" test end-to-end.
/// </summary>
public static class E2eResumeRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);

            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var xtream = profiles.First(p => p.Kind == ProfileKind.Xtream);
            await session.SwitchProfileAsync(xtream.Id, CancellationToken.None);

            var catalog = services.GetRequiredService<ICatalogRepository>();
            var movies = await catalog.GetVodItemsAsync(
                xtream.Id, ContentKind.Movie, null, VodSortOrder.Added, 10, 0, CancellationToken.None);
            report.AppendLine($"movies={movies.Count}");
            if (movies.Count == 0)
            {
                report.AppendLine("RESUME-RESULT=FAIL (no movies)");
                return 1;
            }

            var movie = movies[0];
            var vodService = services.GetRequiredService<VodService>();
            var url = vodService.ResolveMovieUrl(movie, movie.ContainerExtension);
            report.AppendLine($"url={url}");

            var playback = services.GetRequiredService<PlaybackService>();
            var history = services.GetRequiredService<IWatchHistoryRepository>();
            playback.IsMuted = true;

            // 1) Play from the start.
            await playback.PlayVodAsync(new VodPlayRequest(
                url!, ContentKind.Movie, movie.ProviderItemId, movie.Name, movie.PosterUrl, ResumeSeconds: 0),
                CancellationToken.None);
            var playing = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
            report.AppendLine($"play state={playback.State} isVod={playback.IsVod}");

            // 2) Seek to ~10 minutes (600s) once duration is known, let it advance.
            await WaitForAsync(() => playback.DurationSeconds > 0, TimeSpan.FromSeconds(10));
            report.AppendLine($"duration={playback.DurationSeconds:F0}s");
            playback.Seek(600);
            await Task.Delay(2500);
            var positionBeforeStop = playback.PositionSeconds;
            report.AppendLine($"position={positionBeforeStop:F0}s");

            // 3) "Quit".
            playback.Stop();
            await WaitForAsync(() => playback.State == PlaybackState.Idle, TimeSpan.FromSeconds(5));

            // 4) The resume position must have been persisted.
            var saved = await history.GetAsync(xtream.Id, ContentKind.Movie, movie.ProviderItemId, CancellationToken.None);
            var savedPosition = saved?.PositionSeconds ?? 0;
            report.AppendLine($"savedPosition={savedPosition:F0}s");

            // 5) Relaunch with the saved position; the stream should seek back near it.
            await playback.PlayVodAsync(new VodPlayRequest(
                url!, ContentKind.Movie, movie.ProviderItemId, movie.Name, movie.PosterUrl, savedPosition),
                CancellationToken.None);
            await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
            var resumedNear = await WaitForAsync(
                () => playback.PositionSeconds > savedPosition - 60, TimeSpan.FromSeconds(10));
            report.AppendLine($"resumedPosition={playback.PositionSeconds:F0}s resumedNear={resumedNear}");

            playback.Stop();

            var pass = playing && savedPosition > 300 && resumedNear;
            report.AppendLine(pass ? "RESUME-RESULT=PASS" : "RESUME-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E resume run failed");
            report.AppendLine($"RESUME-RESULT=FAIL {ex}");
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

            await Task.Delay(100);
        }

        return condition();
    }
}
