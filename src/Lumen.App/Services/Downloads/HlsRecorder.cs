using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;
using Lumen.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Downloads;

/// <summary>
/// Records an HLS (.m3u8) VOD stream to a local <c>.ts</c> file with a dedicated headless LibVLC
/// stream-output pipeline (LibVLC 3.x has no record API). Modeled on the headless
/// <see cref="Lumen.App.Diagnostics.StreamProbe"/> pattern: its own <see cref="LibVLC"/>, muted,
/// polled state, and disposal in <c>using</c>. It never touches the shared playback player.
/// HLS cannot resume, so a cancelled or failed record deletes its partial and re-records on retry.
/// </summary>
public sealed class HlsRecorder : IVodDownloader
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<HlsRecorder> _logger;

    public HlsRecorder(ILogger<HlsRecorder> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        DownloadContext context, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        Directory.CreateDirectory(Path.GetDirectoryName(context.FinalPath)!);
        var partPath = context.PartPath; // finalPath + ".part.ts"
        TryDelete(partPath);             // HLS can't resume — always start clean

        LibVLCSharp.Shared.Core.Initialize();

        // Never touch audio here: a display-less sout chain renders nothing, and libvlc
        // mute/volume instantiate an audio output that, on Windows, mutes the app's shared
        // WASAPI session — silencing the main player while a download runs. The dummy aout
        // keeps this instance away from the session entirely.
        using var libVlc = new LibVLC("--no-video-title-show", "--quiet", "--aout=dummy");
        using var media = new Media(libVlc, new Uri(context.Url));
        if (!string.IsNullOrWhiteSpace(context.UserAgent))
        {
            media.AddOption($":http-user-agent={context.UserAgent}");
        }

        media.AddOption(":network-caching=3000");
        media.AddOption(SoutString.BuildFileRecord(partPath));
        media.AddOption(":sout-keep");

        // Capture every elementary stream (all audio tracks, subtitles), not just selected ones.
        media.AddOption(":sout-all");

        using var player = new MediaPlayer(libVlc);

        try
        {
            player.Play(media);
            await RecordLoopAsync(player, progress, cancellationToken);

            // Stop to flush the muxer and close the output file before moving it.
            await Task.Run(() => SafeStop(player), CancellationToken.None);

            if (!File.Exists(partPath) || new FileInfo(partPath).Length < 1024)
            {
                TryDelete(partPath);
                throw new InvalidOperationException("The recorded file is empty.");
            }

            File.Move(partPath, context.FinalPath, overwrite: true);
            progress.Report(new DownloadProgress(0, null, 1000));
            _logger.LogInformation("HLS recording complete: {Path}", context.FinalPath);
        }
        catch
        {
            await Task.Run(() => SafeStop(player), CancellationToken.None);
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>Polls player state until the stream ends; reports time-based progress; guards stalls.</summary>
    private static async Task RecordLoopAsync(
        MediaPlayer player, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var hasStarted = false;
        var lastTime = -1L;
        var lastAdvanceMs = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (player.State)
            {
                case VLCState.Ended:
                    return;
                case VLCState.Error:
                    throw new InvalidOperationException("The HLS stream could not be recorded.");
                case VLCState.Playing:
                    hasStarted = true;
                    break;
                default:
                    break;
            }

            var now = stopwatch.ElapsedMilliseconds;
            var length = player.Length;
            var time = player.Time;
            if (length > 0)
            {
                var permille = (int)Math.Clamp(time * 1000 / length, 0, 1000);
                progress.Report(new DownloadProgress(0, null, permille));
            }

            if (time > lastTime)
            {
                lastTime = time;
                lastAdvanceMs = now;
            }

            // Startup guard applies only until Playing is first seen — a later dip into
            // Buffering must not read as "never started". Stall detection takes over from there.
            if (!hasStarted && now > StartupTimeout.TotalMilliseconds)
            {
                throw new InvalidOperationException("The HLS stream did not start.");
            }

            if (hasStarted && now - lastAdvanceMs > StallTimeout.TotalMilliseconds)
            {
                throw new InvalidOperationException("The HLS recording stalled.");
            }

            if (now > MaxDuration.TotalMilliseconds)
            {
                throw new InvalidOperationException("The HLS recording exceeded the maximum duration.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static void SafeStop(MediaPlayer player)
    {
        try
        {
            player.Stop();
        }
        catch (Exception)
        {
            // The player may already be disposed/stopped; nothing more to do.
        }
    }

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
