using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// Background scheduler that checks GitHub for updates shortly after launch and then on the
/// configured cadence. Never runs during diagnostic/screenshot builds. The heavy lifting (compare,
/// download, verify) lives in <see cref="UpdateService"/>; this only drives the timing.
/// </summary>
public sealed class UpdateCheckScheduler : BackgroundService
{
    private readonly UpdateService _updates;
    private readonly ILogger<UpdateCheckScheduler> _logger;

    public UpdateCheckScheduler(UpdateService updates, ILogger<UpdateCheckScheduler> logger)
    {
        _updates = updates;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Diagnostic/e2e runs must stay hermetic — no live network lookups.
        if (App.IsDiagnosticRun)
        {
            return;
        }

        // Let startup settle before the first "on load" check.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _updates.CheckAsync(manual: false, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled update check failed");
            }

            int hours;
            try
            {
                hours = await _updates.GetFrequencyHoursAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                hours = UpdateService.DefaultFrequencyHours;
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 30)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
