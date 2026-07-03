using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.App.Controls.Epg;
using Lumen.Core.Models;
using Lumen.Providers.Xmltv;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Phase-5 gate: builds the custom guide panel with 500 channels × 7 days and pans it,
/// measuring per-frame render time (smoothness), and verifies timezone-offset correctness
/// by parsing +05:30 and −08:00 stamps and confirming they land on the same UTC instant.
/// </summary>
public static class GuideBenchmark
{
    public static async Task<int> RunAsync(string outFile, string? screenshotDir)
    {
        var report = new StringBuilder();
        try
        {
            // --- Timezone correctness: +05:30 and −08:00 must resolve to identical UTC ---
            XmltvTime.TryParse("20260704013000 +0530", out var india);
            XmltvTime.TryParse("20260703120000 -0800", out var la);
            XmltvTime.TryParse("20260703200000 +0000", out var utc);
            var tzOk = india == utc && la == utc;
            report.AppendLine($"tz +0530={india} -0800={la} utc={utc} match={tzOk}");

            // --- Build 500 channels × 7 days of programmes ---
            const int channelCount = 500;
            var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var end = start.AddDays(7);
            var startUnix = start.ToUnixTimeSeconds();

            var rows = new List<GuideRow>(channelCount);
            for (var c = 0; c < channelCount; c++)
            {
                var programmes = new List<Programme>();
                var cursor = startUnix;
                var endUnix = end.ToUnixTimeSeconds();
                var slot = 0;
                while (cursor < endUnix)
                {
                    var duration = (30 + slot % 4 * 15) * 60; // 30–75 min
                    programmes.Add(new Programme
                    {
                        ChannelXmltvId = $"ch{c}",
                        StartUtc = cursor,
                        StopUtc = cursor + duration,
                        Title = $"Show {c}-{slot}",
                        Description = "Benchmark programme.",
                        Category = "General",
                    });
                    cursor += duration;
                    slot++;
                }

                rows.Add(new GuideRow(new Channel { Id = c, Name = $"Channel {c:D3}", Number = c + 1 }, $"ch{c}", programmes));
            }

            var totalProgrammes = rows.Sum(r => r.Programmes.Count);
            report.AppendLine($"channels={channelCount} programmes={totalProgrammes}");

            var panel = new EpgGuidePanel
            {
                Rows = rows,
                TimelineStart = start,
                TimelineEnd = end,
                Now = start.AddHours(30),
                PixelsPerMinute = 6,
            };
            var scroller = new ScrollViewer
            {
                CanContentScroll = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            };
            var window = new Window
            {
                Width = 1280,
                Height = 720,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
                Background = (Brush)Application.Current.FindResource("Lumen.Brush.Bg.Base"),
                Content = scroller,
            };
            window.Show();
            await Yield(DispatcherPriority.ContextIdle);

            // --- Horizontal pan across a day, then vertical pan across channels ---
            var frameTimes = new List<double>();
            var sw = new Stopwatch();
            const int steps = 150;

            for (var i = 0; i < steps; i++)
            {
                sw.Restart();
                scroller.ScrollToHorizontalOffset(i * 40);
                await RenderYield();
                sw.Stop();
                frameTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            for (var i = 0; i < steps; i++)
            {
                sw.Restart();
                scroller.ScrollToVerticalOffset(i * 40);
                await RenderYield();
                sw.Stop();
                frameTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            frameTimes.Sort();
            var median = frameTimes[frameTimes.Count / 2];
            var p95 = frameTimes[(int)(frameTimes.Count * 0.95)];
            var over16 = frameTimes.Count(t => t > 16.7);
            report.AppendLine($"frames={frameTimes.Count} medianMs={median:F2} p95Ms={p95:F2} over16ms={over16}");

            if (screenshotDir is not null)
            {
                scroller.ScrollToHorizontalOffset(30 * 60 * 6); // ~30h in
                scroller.ScrollToVerticalOffset(0);
                await RenderYield();
                await Yield(DispatcherPriority.ContextIdle);
                Directory.CreateDirectory(screenshotDir);
                VisualCapture.SaveWindow(window, Path.Combine(screenshotDir, "guide.png"));
            }

            window.Close();

            var pass = tzOk && p95 < 16.7;
            report.AppendLine(pass ? "GUIDE-RESULT=PASS" : "GUIDE-RESULT=FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Guide benchmark failed");
            report.AppendLine($"GUIDE-RESULT=FAIL {ex}");
            return 1;
        }
        finally
        {
            File.WriteAllText(outFile, report.ToString());
        }
    }

    private static Task Yield(DispatcherPriority priority) =>
        Application.Current.Dispatcher.InvokeAsync(() => { }, priority).Task;

    private static async Task RenderYield()
    {
        var tcs = new TaskCompletionSource();

        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;
        await tcs.Task;
    }
}
