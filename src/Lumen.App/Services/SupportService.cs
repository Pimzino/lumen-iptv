using System.Diagnostics;
using System.Globalization;
using Lumen.Core.Abstractions;
using Serilog;

namespace Lumen.App.Services;

/// <summary>
/// Owns the "Buy me a coffee" support link and the occasional, opt-out support reminder.
/// The reminder is shown at most once a fortnight, seeded so a brand-new user is never nudged
/// on first launch; the caller is responsible for suppressing it while media is playing.
/// </summary>
public sealed class SupportService
{
    /// <summary>Global setting: "false" disables the periodic reminder (default on).</summary>
    public const string ReminderEnabledKey = "support_prompt_enabled";

    /// <summary>Global setting: unix seconds of the last time the reminder was shown.</summary>
    public const string LastShownKey = "support_prompt_last_shown_utc";

    /// <summary>The creator's Buy Me a Coffee page.</summary>
    public const string DonationUrl = "https://www.buymeacoffee.com/Pimzino";

    private static readonly TimeSpan ReminderInterval = TimeSpan.FromDays(14);

    private readonly ISettingsRepository _settings;
    private readonly IClock _clock;

    public SupportService(ISettingsRepository settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
    }

    /// <summary>Opens the donation page in the user's default browser.</summary>
    public void OpenDonationPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open the donation page");
        }
    }

    /// <summary>
    /// True when the periodic reminder is due: enabled, and at least a fortnight since it was last
    /// shown. On first ever launch the timestamp is seeded (and false returned) so the first
    /// reminder appears ~two weeks in rather than immediately.
    /// </summary>
    public async Task<bool> IsReminderDueAsync(CancellationToken cancellationToken)
    {
        if (await _settings.GetAsync(0, ReminderEnabledKey, cancellationToken) == "false")
        {
            return false;
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var lastShownRaw = await _settings.GetAsync(0, LastShownKey, cancellationToken);
        if (!long.TryParse(lastShownRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastShown))
        {
            await MarkShownAsync(cancellationToken);
            return false;
        }

        return now - lastShown >= (long)ReminderInterval.TotalSeconds;
    }

    /// <summary>Records that the reminder was shown now, resetting the fortnightly countdown.</summary>
    public Task MarkShownAsync(CancellationToken cancellationToken) =>
        _settings.SetAsync(
            0,
            LastShownKey,
            _clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            cancellationToken);
}
