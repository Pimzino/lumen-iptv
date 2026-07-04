using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.App.Views.Player;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Headless gate for the player overlay's now-playing identity, asserting the *rendered* elements
/// rather than view-model state: plays a movie and checks the heading TextBlock shows the VOD title
/// with the LIVE badge collapsed, then switches to a live channel and checks the reverse. Requires
/// a database prepared by <c>--e2e</c> and a running DevServer.
/// </summary>
public static class VodUiProbe
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window, string outFile)
    {
        var report = new StringBuilder();
        var faults = new List<string>();

        void OnFault(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            faults.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        }

        App.SuppressCrashDialog = true;
        Application.Current.DispatcherUnhandledException += OnFault;
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            if (session.CurrentProfile is null)
            {
                await session.InitializeAsync(CancellationToken.None);
            }

            if (session.CurrentProfile is not { } profile)
            {
                report.AppendLine("VODUI-RESULT=FAIL no-profile");
                return 1;
            }

            var catalog = services.GetRequiredService<ICatalogRepository>();
            var movies = await catalog.GetVodItemsAsync(
                profile.Id, ContentKind.Movie, null, null, VodSortOrder.Added, 10, 0, CancellationToken.None);
            var channels = await catalog.GetChannelsAsync(profile.Id, null, CancellationToken.None);
            report.AppendLine($"movies={movies.Count} channels={channels.Count}");
            if (movies.Count == 0 || channels.Count == 0)
            {
                report.AppendLine("VODUI-RESULT=FAIL empty-catalog");
                return 1;
            }

            var movie = movies[0];
            var url = services.GetRequiredService<VodService>()
                .ResolveMovieUrl(movie, movie.ContainerExtension);
            if (url is null)
            {
                report.AppendLine("VODUI-RESULT=FAIL no-url");
                return 1;
            }

            var playback = services.GetRequiredService<PlaybackService>();
            playback.IsMuted = true;

            // 1) Play a movie; PlayVodAsync enters the full player itself.
            await playback.PlayVodAsync(new VodPlayRequest(
                url, ContentKind.Movie, movie.ProviderItemId, movie.Name, movie.PosterUrl, ResumeSeconds: 0),
                CancellationToken.None);
            var vodPlaying = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
            for (var i = 0; i < 5; i++)
            {
                await PumpAsync(window);
            }

            var overlay = window.Dispatcher.Invoke(() => playback.OverlayForDiagnostics as PlayerOverlayView);
            if (overlay is null)
            {
                report.AppendLine($"VODUI-RESULT=FAIL overlay-not-found state={playback.State}");
                return 1;
            }

            var (vodTitle, vodBadge, vodLoaded) = ReadOverlay(window, overlay);
            var vodTitleOk = vodPlaying && vodTitle == movie.Name;
            var vodBadgeOk = vodBadge == Visibility.Collapsed;
            report.AppendLine($"vod state={playback.State} overlayLoaded={vodLoaded}");
            report.AppendLine($"vod heading=\"{vodTitle}\" expected=\"{movie.Name}\" ok={vodTitleOk}");
            report.AppendLine($"vod liveBadge={vodBadge} ok={vodBadgeOk}");

            // 1b) The timeline must be rendered and scrubbable: visible, sized to the real
            // duration once the position timer resolves it, and a Seek must move the
            // rendered slider (not just the service state).
            await WaitForAsync(() => playback.DurationSeconds > 0, TimeSpan.FromSeconds(10));
            await PumpAsync(window);
            var (seekVisibility, seekMax, _, seekDurationText) = ReadSeekBar(window, overlay);
            var seekVisibleOk = seekVisibility == Visibility.Visible;
            var seekMaxOk = playback.DurationSeconds > 0
                && Math.Abs(seekMax - playback.DurationSeconds) < 2;
            report.AppendLine($"vod seekBar={seekVisibility} ok={seekVisibleOk}");
            report.AppendLine(
                $"vod seekMax={seekMax:F0} duration={playback.DurationSeconds:F0} " +
                $"durationText=\"{seekDurationText}\" ok={seekMaxOk}");

            var seekTarget = playback.DurationSeconds / 2;
            playback.Seek(seekTarget);
            await PumpAsync(window);
            var (_, _, seekValue, _) = ReadSeekBar(window, overlay);
            var seekMovedOk = Math.Abs(seekValue - seekTarget) < 5;
            report.AppendLine($"vod seekValue={seekValue:F0} target={seekTarget:F0} ok={seekMovedOk}");

            // 2) Switch to a live channel in-place; the heading must follow and the badge return.
            await playback.PlayChannelAsync(channels[0], channels, preview: false, CancellationToken.None);
            var livePlaying = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));
            for (var i = 0; i < 5; i++)
            {
                await PumpAsync(window);
            }

            var (liveTitle, liveBadge, _) = ReadOverlay(window, overlay);
            var liveTitleOk = livePlaying && liveTitle == channels[0].Name;
            var liveBadgeOk = liveBadge == Visibility.Visible;
            report.AppendLine($"live heading=\"{liveTitle}\" expected=\"{channels[0].Name}\" ok={liveTitleOk}");
            report.AppendLine($"live liveBadge={liveBadge} ok={liveBadgeOk}");
            report.AppendLine($"live isVod={playback.IsVod}");

            // Live streams are not seekable — the timeline must leave with the VOD.
            var (liveSeekVisibility, _, _, _) = ReadSeekBar(window, overlay);
            var liveSeekOk = liveSeekVisibility == Visibility.Collapsed;
            report.AppendLine($"live seekBar={liveSeekVisibility} ok={liveSeekOk}");

            playback.Stop();
            await PumpAsync(window);

            foreach (var fault in faults)
            {
                report.AppendLine($"ERROR {fault}");
            }

            var pass = vodTitleOk && vodBadgeOk
                && seekVisibleOk && seekMaxOk && seekMovedOk
                && liveTitleOk && liveBadgeOk && liveSeekOk
                && !playback.IsVod && faults.Count == 0;
            report.AppendLine(pass ? "VODUI-RESULT=PASS" : "VODUI-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VOD UI probe failed");
            report.AppendLine($"VODUI-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            Application.Current.DispatcherUnhandledException -= OnFault;
            App.SuppressCrashDialog = false;
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static (string Title, Visibility Badge, bool Loaded) ReadOverlay(
        Window window, PlayerOverlayView overlay) =>
        window.Dispatcher.Invoke(() =>
            (overlay.NowPlayingTitleText.Text, overlay.LiveBadge.Visibility, overlay.IsLoaded));

    private static (Visibility Visibility, double Maximum, double Value, string DurationText) ReadSeekBar(
        Window window, PlayerOverlayView overlay) =>
        window.Dispatcher.Invoke(() =>
            (overlay.SeekBar.Visibility, overlay.SeekSlider.Maximum,
             overlay.SeekSlider.Value, overlay.SeekDurationText.Text));

    private static async Task PumpAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
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
