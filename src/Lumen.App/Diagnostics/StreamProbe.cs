using System.Diagnostics;
using System.IO;
using System.Text;
using LibVLCSharp.Shared;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Lumen.App.Diagnostics;

/// <summary>
/// Diagnoses "streams won't connect" by resolving the current profile's first live channel (and a
/// movie, if available) and attempting playback through LibVLC with a matrix of options — default,
/// several User-Agents, HTTP reconnect, and HLS — while capturing LibVLC's own log output. The
/// report shows which combination reaches <c>Playing</c> and the error text (e.g. HTTP 403) for the
/// ones that fail, so the fix is evidence-driven rather than guessed. Credentials are redacted.
/// </summary>
public static class StreamProbe
{
    public static async Task<int> RunAsync(IServiceProvider services, string outFile)
    {
        var report = new StringBuilder();
        LibVLC? libVlc = null;
        MediaPlayer? player = null;
        var logBuffer = new List<string>();
        try
        {
            var session = services.GetRequiredService<ISessionService>();
            await session.InitializeAsync(CancellationToken.None);
            var profile = session.CurrentProfile;
            if (profile is null)
            {
                report.AppendLine("PROBE-RESULT=FAIL no-profile");
                return 1;
            }

            report.AppendLine($"profile={profile.Name} kind={profile.Kind} preferHls={profile.PreferHls}");

            var channels = await services.GetRequiredService<ICatalogRepository>()
                .GetChannelsAsync(profile.Id, null, CancellationToken.None);
            report.AppendLine($"channels={channels.Count}");

            // Redaction: strip credentials from any URL we print.
            var credentials = session.GetXtreamCredentials(profile);
            string Redact(string url)
            {
                if (credentials is null)
                {
                    return url;
                }

                return url
                    .Replace(Uri.EscapeDataString(credentials.Username), "USER", StringComparison.Ordinal)
                    .Replace(credentials.Username, "USER", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(credentials.Password), "PASS", StringComparison.Ordinal)
                    .Replace(credentials.Password, "PASS", StringComparison.Ordinal);
            }

            // Resolve a live TS URL and its HLS variant from the first channel that has a stream.
            var channel = channels.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.StreamUrl) || !string.IsNullOrWhiteSpace(c.ProviderStreamId));
            if (channel is null)
            {
                report.AppendLine("PROBE-RESULT=FAIL no-playable-channel");
                return 1;
            }

            string? liveTs;
            string? liveHls = null;
            if (!string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                liveTs = channel.StreamUrl;
            }
            else if (credentials is not null && channel.ProviderStreamId is not null)
            {
                liveTs = XtreamUrls.Live(credentials.Server, credentials.Username, credentials.Password,
                    channel.ProviderStreamId, LiveStreamContainer.MpegTs).AbsoluteUri;
                liveHls = XtreamUrls.Live(credentials.Server, credentials.Username, credentials.Password,
                    channel.ProviderStreamId, LiveStreamContainer.Hls).AbsoluteUri;
            }
            else
            {
                report.AppendLine("PROBE-RESULT=FAIL cannot-resolve-url");
                return 1;
            }

            report.AppendLine($"liveChannel=\"{channel.Name}\"");
            report.AppendLine($"liveUrl={Redact(liveTs)}");
            report.AppendLine();

            LibVLCSharp.Shared.Core.Initialize();
            libVlc = new LibVLC("--verbose=2", "--no-video-title-show");
            libVlc.Log += (_, e) =>
            {
                if (e.Level >= LogLevel.Warning)
                {
                    lock (logBuffer)
                    {
                        logBuffer.Add($"[{e.Level}] {e.Module}: {e.Message}");
                    }
                }
            };
            player = new MediaPlayer(libVlc) { Mute = true };

            async Task<string> TryAsync(string label, string url, params string[] options)
            {
                lock (logBuffer)
                {
                    logBuffer.Clear();
                }

                using var media = new Media(libVlc!, new Uri(url));
                media.AddOption(":network-caching=2000");
                foreach (var option in options)
                {
                    media.AddOption(option);
                }

                player!.Stop();
                player.Play(media);

                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(12))
                {
                    var state = player.State;
                    if (state is VLCState.Playing or VLCState.Error or VLCState.Ended)
                    {
                        break;
                    }

                    await Task.Delay(150);
                }

                var final = player.State;
                player.Stop();

                string diagnostics;
                lock (logBuffer)
                {
                    // Keep the lines most likely to explain a failure.
                    var relevant = logBuffer
                        .Where(l => l.Contains("http", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("access", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("tls", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("40", StringComparison.Ordinal)
                            || l.Contains("50", StringComparison.Ordinal))
                        .Distinct()
                        .TakeLast(4)
                        .ToList();
                    diagnostics = relevant.Count > 0 ? string.Join(" | ", relevant) : "(no warnings logged)";
                }

                var verdict = final == VLCState.Playing ? "PLAYING" : final.ToString().ToUpperInvariant();
                report.AppendLine($"[{label}] -> {verdict}");
                report.AppendLine($"    {Redact(diagnostics)}");
                return verdict;
            }

            // Matrix on the live stream: default first, then User-Agent variants, then reconnect.
            var results = new List<(string Label, string Verdict)>
            {
                ("live default", await TryAsync("live default", liveTs)),
                ("live ua=vlc", await TryAsync("live ua=vlc", liveTs, ":http-user-agent=VLC/3.0.20 LibVLC/3.0.20")),
                ("live ua=browser", await TryAsync("live ua=browser", liveTs,
                    ":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36")),
                ("live ua=okhttp", await TryAsync("live ua=okhttp", liveTs, ":http-user-agent=okhttp/4.9.3")),
                ("live ua=smarters", await TryAsync("live ua=smarters", liveTs, ":http-user-agent=IPTVSmartersPlayer")),
                ("live reconnect", await TryAsync("live reconnect", liveTs, ":http-reconnect")),
            };

            if (liveHls is not null)
            {
                results.Add(("live hls default", await TryAsync("live hls default", liveHls)));
            }

            // End-to-end: play the same channel through the real PlaybackService, which now applies
            // the default UA. This exercises the actual app code path, not a hand-built Media.
            try
            {
                var playback = services.GetRequiredService<PlaybackService>();
                playback.IsMuted = true;
                await playback.PlayChannelAsync(channel, channels, preview: false, CancellationToken.None);
                var ok = await WaitForAsync(
                    () => playback.State == PlaybackState.Playing, TimeSpan.FromSeconds(15));
                report.AppendLine();
                report.AppendLine($"appPath={(ok ? "PLAYING" : playback.State.ToString())} " +
                    $"(default UA \"{Profile.DefaultStreamUserAgent}\")");
                playback.Stop();
            }
            catch (Exception ex)
            {
                report.AppendLine($"appPath=ERROR {ex.Message}");
            }

            report.AppendLine();
            var anyPlaying = results.Any(r => r.Verdict == "PLAYING");
            var defaultPlaying = results.First().Verdict == "PLAYING";
            report.AppendLine($"anyPlaying={anyPlaying} defaultPlaying={defaultPlaying}");
            if (anyPlaying)
            {
                report.AppendLine("workingCombos=" + string.Join(", ", results.Where(r => r.Verdict == "PLAYING").Select(r => r.Label)));
            }

            report.AppendLine(anyPlaying ? "PROBE-RESULT=PASS" : "PROBE-RESULT=FAIL");
            return anyPlaying ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Stream probe failed");
            report.AppendLine($"PROBE-RESULT=FAIL {ex.Message}");
            return 1;
        }
        finally
        {
            player?.Dispose();
            libVlc?.Dispose();
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

            await Task.Delay(150);
        }

        return condition();
    }
}
