using Lumen.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services.Trakt;

/// <summary>
/// Periodic Trakt sync, mirroring <see cref="EpgRefreshScheduler"/>: a short startup delay
/// (the host starts before the database is migrated), then a fast tick that runs a sync when
/// the last one is old enough. The sync service itself is single-flight and last_activities-
/// gated, so ticks are cheap.
/// </summary>
public sealed class TraktSyncScheduler : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(6);

    private readonly TraktSyncService _sync;
    private readonly IClock _clock;
    private readonly ILogger<TraktSyncScheduler> _logger;

    public TraktSyncScheduler(TraktSyncService sync, IClock clock, ILogger<TraktSyncScheduler> logger)
    {
        _sync = sync;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await _sync.IsSyncEnabledAsync(stoppingToken).ConfigureAwait(false))
                    {
                        var last = await _sync.GetLastSyncUtcAsync(stoppingToken).ConfigureAwait(false);
                        var age = _clock.UtcNow.ToUnixTimeSeconds() - last;
                        if (age >= SyncInterval.TotalSeconds)
                        {
                            await _sync.SyncNowAsync(force: false, stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled Trakt sync tick failed");
                }

                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
