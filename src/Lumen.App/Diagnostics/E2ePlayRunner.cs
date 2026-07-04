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
/// Headless playback gate: plays fixture streams through the real PlaybackService,
/// verifies the Playing state, channel zapping, automatic reconnect with backoff after a
/// forced stream drop, and clean stop. Requires a database prepared by <c>--e2e</c> and a
/// running DevServer.
/// </summary>
public static class E2ePlayRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);

            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var m3uProfile = profiles.First(p => p.Kind == ProfileKind.M3u);
            await session.SwitchProfileAsync(m3uProfile.Id, CancellationToken.None);

            var channels = await services.GetRequiredService<ICatalogRepository>()
                .GetChannelsAsync(m3uProfile.Id, null, CancellationToken.None);
            report.AppendLine($"channels={channels.Count}");

            var playback = services.GetRequiredService<PlaybackService>();

            // 1) Open a stable stream and reach Playing.
            playback.IsMuted = true;
            await playback.PlayChannelAsync(channels[0], channels, preview: false, CancellationToken.None);
            var played = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));
            report.AppendLine($"play state={playback.State} channel={playback.CurrentChannel?.Name}");

            // 1b) Dwelling on a channel (past the zap filter) must record it into watch
            //     history — the Home page's "Recently watched" source.
            var history = services.GetRequiredService<IWatchHistoryRepository>();
            var historyKey = channels[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var recorded = false;
            var dwell = Stopwatch.StartNew();
            while (dwell.Elapsed < TimeSpan.FromSeconds(25) && !recorded)
            {
                recorded = await history.GetAsync(
                    m3uProfile.Id, ContentKind.Live, historyKey, CancellationToken.None) is not null;
                if (!recorded)
                {
                    await Task.Delay(500);
                }
            }

            report.AppendLine($"watch-history recorded={recorded}");

            // 2) Zap to the next channel.
            var before = playback.CurrentChannel?.Id;
            await playback.ZapAsync(+1);
            var zapped = await WaitForAsync(
                () => playback.State == PlaybackState.Playing && playback.CurrentChannel?.Id != before,
                TimeSpan.FromSeconds(15));
            report.AppendLine($"zap state={playback.State} channel={playback.CurrentChannel?.Name}");

            // 3) Play the deliberately flaky stream (drops ~8s in) and watch the reconnect loop recover.
            var flaky = channels.First(c => c.StreamUrl?.Contains("105", StringComparison.Ordinal) == true);
            await playback.PlayChannelAsync(flaky, channels, preview: false, CancellationToken.None);
            await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));

            var sawReconnect = await WaitForAsync(
                () => playback.State == PlaybackState.Reconnecting, TimeSpan.FromSeconds(25));
            var attemptSeen = playback.ReconnectAttempt;
            var recovered = sawReconnect && await WaitForAsync(
                () => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(30));
            report.AppendLine($"reconnect seen={sawReconnect} attempt={attemptSeen} recovered={recovered}");

            // 4) Clean stop.
            playback.Stop();
            var stopped = await WaitForAsync(() => playback.State == PlaybackState.Idle, TimeSpan.FromSeconds(5));
            report.AppendLine($"stop state={playback.State}");

            var pass = played && recorded && zapped && sawReconnect && attemptSeen >= 1 && recovered && stopped;
            report.AppendLine(pass ? "PLAY-RESULT=PASS" : "PLAY-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E playback run failed");
            report.AppendLine($"PLAY-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
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
