using System.Globalization;

namespace Lumen.Providers.Xtream;

/// <summary>
/// Converts UTC instants into an Xtream panel's local wall-clock time. Timeshift URLs carry
/// their start time in the <b>panel's</b> timezone, so getting this wrong plays the wrong hour.
/// </summary>
public static class XtreamServerTime
{
    /// <summary>
    /// Converts <paramref name="utc"/> to the panel's local time. Prefers the IANA
    /// <c>timezone</c> from server_info (DST-correct for past instants); falls back to the
    /// offset implied by <c>time_now</c> vs <c>timestamp_now</c>; last resort is UTC as-is.
    /// </summary>
    public static DateTime ToServerLocal(DateTimeOffset utc, XtreamServerInfo? serverInfo)
    {
        if (!string.IsNullOrWhiteSpace(serverInfo?.Timezone))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(serverInfo.Timezone);
                return TimeZoneInfo.ConvertTime(utc, zone).DateTime;
            }
            catch (TimeZoneNotFoundException)
            {
                // Unrecognized id (custom panel value) — fall through to the offset heuristic.
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (CurrentOffset(serverInfo) is { } offset)
        {
            return (utc + offset).UtcDateTime;
        }

        return utc.UtcDateTime;
    }

    /// <summary>
    /// The panel's current UTC offset implied by comparing its local clock string
    /// (<c>time_now</c>) with its unix clock (<c>timestamp_now</c>), rounded to minutes.
    /// Null when either half is missing or unparseable.
    /// </summary>
    internal static TimeSpan? CurrentOffset(XtreamServerInfo? serverInfo)
    {
        if (serverInfo is not { TimeNow: { } timeNow, TimestampNow: > 0 and var unixNow })
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"];
        if (!DateTime.TryParseExact(
                timeNow.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var serverLocal))
        {
            return null;
        }

        var serverUtc = DateTimeOffset.FromUnixTimeSeconds(unixNow).UtcDateTime;
        var offset = serverLocal - serverUtc;

        // Round to whole minutes: the two clock fields are written a beat apart on the panel.
        return TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes));
    }
}
