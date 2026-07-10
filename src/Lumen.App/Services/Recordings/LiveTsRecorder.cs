using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;
using Lumen.App.Services.Playback;
using Lumen.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Recordings;

/// <summary>A live-capture progress tick: wall-clock elapsed and bytes on disk so far.</summary>
public sealed record RecordingProgress(long ElapsedSeconds, long SizeBytes);

/// <summary>What a finished capture produced (drives the row's duration/size at finalize).</summary>
public sealed record RecordingCaptureResult(long DurationSeconds, long SizeBytes);

/// <summary>
/// Captures a live stream to a local MPEG-TS file with a dedicated headless LibVLC stream-output
/// pipeline (same mechanics as <see cref="Downloads.HlsRecorder"/>), but with live semantics:
/// the stream has no end, so <b>cancellation means "stop and keep"</b> — the partial file is
/// finalized, never deleted. Only a stream that never starts (or captures nothing) fails.
/// </summary>
public sealed class LiveTsRecorder
{
    /// <summary>Below this the capture is considered empty and a finalize turns into a failure.</summary>
    private const long MinimumCaptureBytes = 4096;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<LiveTsRecorder> _logger;

    public LiveTsRecorder(ILogger<LiveTsRecorder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records until <paramref name="cancellationToken"/> fires (the user's Stop / app exit),
    /// the stall watchdog trips, or the safety cap is reached — all of which finalize the file.
    /// Throws when the stream never starts or nothing usable was captured.
    /// </summary>
    public async Task<RecordingCaptureResult> RunAsync(
        LiveMediaRequest media,
        string partPath,
        string finalPath,
        IProgress<RecordingProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(progress);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        TryDelete(partPath);

        LibVLCSharp.Shared.Core.Initialize();

        // A display-less sout chain renders nothing, so this pipeline must never touch audio:
        // libvlc mute/volume instantiate an audio output on demand, and on Windows that output
        // applies mute at the app's shared WASAPI session — silencing the main player. The dummy
        // aout makes it impossible for this instance to reach the session at all.
        using var libVlc = new LibVLC("--no-video-title-show", "--quiet", "--aout=dummy");
        using var vlcMedia = new Media(libVlc, new Uri(media.Url));
        vlcMedia.AddOption($":http-user-agent={media.UserAgent}");
        if (!string.IsNullOrEmpty(media.Referrer))
        {
            vlcMedia.AddOption($":http-referrer={media.Referrer}");
        }

        vlcMedia.AddOption(":network-caching=3000");
        vlcMedia.AddOption(SoutString.BuildFileRecord(partPath));
        vlcMedia.AddOption(":sout-keep");

        // Capture every elementary stream (all audio tracks, subtitles), not just the selected
        // ones — a recording should preserve the broadcast, and selection is meaningless headless.
        vlcMedia.AddOption(":sout-all");

        using var player = new MediaPlayer(libVlc);

        try
        {
            player.Play(vlcMedia);
            var elapsed = await CaptureLoopAsync(player, partPath, progress, cancellationToken);

            // Stop flushes and closes the muxer so the partial is complete on disk.
            await Task.Run(() => SafeStop(player), CancellationToken.None);

            var size = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (size < MinimumCaptureBytes)
            {
                TryDelete(partPath);
                throw new InvalidOperationException("Nothing was captured from the stream.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            _logger.LogInformation(
                "Live recording finalized: {Path} ({Seconds}s, {Bytes} bytes)", finalPath, elapsed, size);
            return new RecordingCaptureResult(elapsed, size);
        }
        catch
        {
            // Every throw path means no usable capture (finalizable stops return normally):
            // stop the pipeline and drop the partial.
            await Task.Run(() => SafeStop(player), CancellationToken.None);
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>
    /// Polls until a stop condition; returns elapsed seconds. A cancellation request returns
    /// normally (stop-and-keep); watchdog trips throw only when nothing was captured yet.
    /// </summary>
    private static async Task<long> CaptureLoopAsync(
        MediaPlayer player, string partPath, IProgress<RecordingProgress> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var hasStarted = false;
        var lastSize = 0L;
        var lastGrowthMs = 0L;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return stopwatch.ElapsedMilliseconds / 1000; // user stop / app exit → finalize
            }

            var now = stopwatch.ElapsedMilliseconds;
            var size = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (size > lastSize)
            {
                lastSize = size;
                lastGrowthMs = now;
            }

            switch (player.State)
            {
                case VLCState.Ended:
                    // A "live" stream that ends server-side: keep whatever was captured.
                    return now / 1000;
                case VLCState.Error when lastSize >= MinimumCaptureBytes:
                    return now / 1000; // the connection died mid-capture — the file is still good
                case VLCState.Error:
                    throw new InvalidOperationException("The stream could not be opened.");
                case VLCState.Playing:
                    hasStarted = true;
                    break;
                default:
                    break;
            }

            if (!hasStarted && now > StartupTimeout.TotalMilliseconds)
            {
                throw new InvalidOperationException("The stream did not start.");
            }

            // No bytes landing for a minute: treat as a dead source. Keep a substantial capture.
            if (hasStarted && now - lastGrowthMs > StallTimeout.TotalMilliseconds)
            {
                if (lastSize >= MinimumCaptureBytes)
                {
                    return now / 1000;
                }

                throw new InvalidOperationException("The stream stalled before anything was captured.");
            }

            if (now > MaxDuration.TotalMilliseconds)
            {
                return now / 1000; // safety cap on a forgotten recording → finalize
            }

            progress.Report(new RecordingProgress(now / 1000, size));

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return stopwatch.ElapsedMilliseconds / 1000;
            }
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
            // Already stopped/disposed; nothing more to flush.
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
