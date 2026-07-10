using System.Diagnostics;
using System.IO;
using System.Text;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.App.Services.Recordings;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Live-recording gate: records the dev server's MPEG-TS live channel for ~10 seconds through the
/// real <see cref="RecordingService"/> + <see cref="LiveTsRecorder"/> pipeline, stops it (stop =
/// finalize), then plays the captured file back offline and removes it. Exercises the second
/// connection, the sout capture, the finalize semantics, and the file:// playback path.
/// </summary>
public static class E2eRecordRunner
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

            var channels = await services.GetRequiredService<ICatalogRepository>()
                .GetChannelsAsync(xtream.Id, null, CancellationToken.None);
            var channel = channels.FirstOrDefault(c => c.ProviderStreamId == "103");
            report.AppendLine($"channels={channels.Count} tsChannel={channel?.Name ?? "(none)"}");
            if (channel is null)
            {
                report.AppendLine("RECORD-RESULT=FAIL (no TS fixture channel)");
                return 1;
            }

            var recordings = services.GetRequiredService<RecordingService>();
            await recordings.DeleteProfileRecordingsAsync(xtream.Id, CancellationToken.None);

            // 1) Start recording on its own connection.
            var outcome = await recordings.StartRecordingAsync(
                channel, "Fixture Programme", xtream.Id, CancellationToken.None);
            report.AppendLine($"start={outcome.Status}");
            if (outcome.Status != RecordStartStatus.Started || outcome.Row is not { } row)
            {
                report.AppendLine("RECORD-RESULT=FAIL (did not start)");
                return 1;
            }

            // A second press while busy must not start another capture.
            var second = await recordings.StartRecordingAsync(
                channel, null, xtream.Id, CancellationToken.None);
            var busyOk = second.Status == RecordStartStatus.Busy;
            report.AppendLine($"busyGuard={busyOk}");

            // 2) Bytes must land on disk while recording.
            var growing = await WaitForAsync(() => row.SizeBytes > 0, TimeSpan.FromSeconds(20));
            report.AppendLine($"capturing={growing} elapsed={row.ElapsedSeconds}s size={row.SizeBytes}");

            await Task.Delay(TimeSpan.FromSeconds(10));

            // 3) Stop = finalize.
            await recordings.StopRecordingAsync(row.Id);
            report.AppendLine($"stopped status={row.Status} duration={row.Item.DurationSeconds}s " +
                $"size={row.Item.SizeBytes} error={row.Error}");
            var completed = row.IsCompleted;
            var fileSize = File.Exists(row.FilePath) ? new FileInfo(row.FilePath).Length : 0;
            report.AppendLine($"file=\"{row.FilePath}\" size={fileSize}");
            var durationOk = (row.Item.DurationSeconds ?? 0) >= 8;

            // 4) Play the capture offline. ContentKind.Live keeps it out of Trakt.
            var playing = false;
            if (completed && fileSize > 0)
            {
                var playback = services.GetRequiredService<PlaybackService>();
                playback.IsMuted = true;
                var url = new Uri(row.FilePath).AbsoluteUri;
                report.AppendLine($"offlineUrl={url}");
                await playback.PlayVodAsync(
                    new VodPlayRequest(url, ContentKind.Live, $"rec:{row.Id}", row.Title, row.LogoUrl, 0),
                    CancellationToken.None);
                playing = await WaitForAsync(() => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(20));
                report.AppendLine($"offlinePlay state={playback.State} playing={playing}");
                playback.Stop();
            }

            // 5) Remove cleans the file and the row.
            await recordings.RemoveAsync(row.Id, CancellationToken.None);
            var removed = !File.Exists(row.FilePath);
            report.AppendLine($"removed={removed}");

            var pass = busyOk && growing && completed && fileSize > 50_000 && durationOk && playing && removed;
            report.AppendLine(pass ? "RECORD-RESULT=PASS" : "RECORD-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "E2E record run failed");
            report.AppendLine($"RECORD-RESULT=FAIL {ex}");
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
