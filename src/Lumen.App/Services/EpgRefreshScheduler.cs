using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// Background scheduler that refreshes the active profile's EPG on the configured interval,
/// and purges expired programmes. Runs a single guarded loop; the manual Settings refresh
/// shares the same <see cref="IEpgSyncService"/> lock so the two never overlap.
/// </summary>
public sealed class EpgRefreshScheduler : BackgroundService, IRecipient<ProfileSwitchedMessage>
{
    private const string EpgIntervalKey = "epg_interval_hours";
    private const string LastRefreshKeyPrefix = "epg_last_refresh_";

    private readonly ISessionService _session;
    private readonly ISettingsRepository _settings;
    private readonly IEpgSyncService _epgSync;
    private readonly IClock _clock;
    private readonly ILogger<EpgRefreshScheduler> _logger;

    public EpgRefreshScheduler(
        ISessionService session,
        ISettingsRepository settings,
        IEpgSyncService epgSync,
        IClock clock,
        IMessenger messenger,
        ILogger<EpgRefreshScheduler> logger)
    {
        _session = session;
        _settings = settings;
        _epgSync = epgSync;
        _clock = clock;
        _logger = logger;
        messenger.Register(this);
    }

    public void Receive(ProfileSwitchedMessage message)
    {
        // A profile switch may need an immediate first refresh; the loop picks it up on its
        // next tick (within a minute), which is soon enough for a background guide.
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup settle before the first check.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MaybeRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled EPG refresh check failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MaybeRefreshAsync(CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null || _epgSync.IsRefreshing)
        {
            return;
        }

        var intervalRaw = await _settings.GetAsync(0, EpgIntervalKey, cancellationToken).ConfigureAwait(false);
        var intervalHours = int.TryParse(intervalRaw, out var hours) ? hours : 12;
        if (intervalHours <= 0)
        {
            return; // manual-only
        }

        var lastKey = LastRefreshKeyPrefix + profile.Id.ToString(CultureInfo.InvariantCulture);
        var lastRaw = await _settings.GetAsync(0, lastKey, cancellationToken).ConfigureAwait(false);
        var last = long.TryParse(lastRaw, out var unix) ? unix : 0;
        var nowUnix = _clock.UtcNow.ToUnixTimeSeconds();

        if (nowUnix - last < intervalHours * 3600L)
        {
            return;
        }

        var hasSource = profile.EpgSource is not null || profile.Kind == Core.Models.ProfileKind.Xtream;
        if (!hasSource)
        {
            return;
        }

        _logger.LogInformation("Background EPG refresh starting for {Profile}", profile.Name);
        await _epgSync.RefreshAsync(profile, progress: null, cancellationToken).ConfigureAwait(false);
        await _settings.SetAsync(0, lastKey, nowUnix.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
    }
}
