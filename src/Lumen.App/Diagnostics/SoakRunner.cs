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
/// Phase-8 gate: a channel-zap soak test. Repeatedly plays and switches channels for a set
/// duration, sampling the managed heap and working set to prove memory stays flat (no leak from
/// media churn). Duration is configurable so CI can run a short version and a manual run the full
/// 30 minutes.
/// </summary>
public static class SoakRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, TimeSpan duration, string outFile)
    {
        var report = new StringBuilder();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);

            var profiles = await services.GetRequiredService<IProfileRepository>().GetAllAsync(CancellationToken.None);
            var m3u = profiles.First(p => p.Kind == ProfileKind.M3u);
            await session.SwitchProfileAsync(m3u.Id, CancellationToken.None);

            var channels = await services.GetRequiredService<ICatalogRepository>()
                .GetChannelsAsync(m3u.Id, null, CancellationToken.None);
            if (channels.Count == 0)
            {
                report.AppendLine("SOAK-RESULT=FAIL (no channels)");
                return 1;
            }

            var playback = services.GetRequiredService<PlaybackService>();
            playback.IsMuted = true;

            // Warm up and let the first stream settle + managed heap reach steady state.
            await playback.PlayChannelAsync(channels[0], channels, preview: false, CancellationToken.None);
            await Task.Delay(3000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var baselineManaged = GC.GetTotalMemory(true);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var baselineWorkingSet = process.WorkingSet64;
            report.AppendLine($"baseline managed={Bytes(baselineManaged)} workingSet={Bytes(baselineWorkingSet)}");

            var stopwatch = Stopwatch.StartNew();
            var zaps = 0;
            long peakManaged = baselineManaged;
            long peakWorkingSet = baselineWorkingSet;
            var index = 0;

            while (stopwatch.Elapsed < duration)
            {
                index = (index + 1) % channels.Count;
                await playback.PlayChannelAsync(channels[index], channels, preview: false, CancellationToken.None);
                zaps++;

                // Zap roughly every 1.5s — aggressive media churn.
                await Task.Delay(1500);

                if (zaps % 20 == 0)
                {
                    process.Refresh();
                    var managed = GC.GetTotalMemory(false);
                    peakManaged = Math.Max(peakManaged, managed);
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    report.AppendLine(
                        $"t={stopwatch.Elapsed.TotalMinutes:F1}m zaps={zaps} managed={Bytes(managed)} workingSet={Bytes(process.WorkingSet64)}");
                }
            }

            playback.Stop();
            await Task.Delay(1000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var finalManaged = GC.GetTotalMemory(true);
            process.Refresh();
            var finalWorkingSet = process.WorkingSet64;
            report.AppendLine($"final managed={Bytes(finalManaged)} workingSet={Bytes(finalWorkingSet)}");
            report.AppendLine($"zaps={zaps}");
            report.AppendLine($"peak managed={Bytes(peakManaged)} workingSet={Bytes(peakWorkingSet)}");

            // "Flat" = managed heap after GC grew by less than 50MB over the run, and no unbounded
            // working-set climb (allow 250MB slack for LibVLC decode buffers + fragmentation).
            var managedGrowth = finalManaged - baselineManaged;
            var workingSetGrowth = finalWorkingSet - baselineWorkingSet;
            report.AppendLine($"managedGrowth={Bytes(managedGrowth)} workingSetGrowth={Bytes(workingSetGrowth)}");

            var pass = managedGrowth < 50L * 1024 * 1024 && workingSetGrowth < 250L * 1024 * 1024;
            report.AppendLine(pass ? "SOAK-RESULT=PASS" : "SOAK-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Soak run failed");
            report.AppendLine($"SOAK-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static string Bytes(long bytes) => $"{bytes / (1024.0 * 1024):F1}MB";
}
