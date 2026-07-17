using System;
using System.Globalization;

namespace POE2Radar.Core.SessionWidget;

/// <summary>
/// A single chip value for the session stat widget — an identifier paired with its
/// formatted display text.
/// </summary>
public sealed record SessionMetricValue(string Id, string DisplayText);

/// <summary>
/// Pure formatting functions that produce the six session stat chip values from
/// their respective data sources. No I/O, no memory access — only string
/// formatting and derivation.
/// </summary>
public static class SessionMetricProviders
{
    /// <summary>
    /// Format the drop count as a plain number with thousand separators.
    /// </summary>
    public static SessionMetricValue Drops(int dropCount)
    {
        var display = dropCount.ToString("N0", CultureInfo.InvariantCulture);
        return new SessionMetricValue("drops", display);
    }

    /// <summary>
    /// Format the XP delta with an explicit sign prefix. Positive values carry a
    /// leading "+"; negative values carry "-"; zero displays as "0".
    /// </summary>
    public static SessionMetricValue XpGained(long xpDelta)
    {
        string display;
        if (xpDelta > 0)
            display = "+" + xpDelta.ToString("N0", CultureInfo.InvariantCulture);
        else if (xpDelta < 0)
            display = xpDelta.ToString("N0", CultureInfo.InvariantCulture);
        else
            display = "0";
        return new SessionMetricValue("xp-gained", display);
    }

    /// <summary>
    /// Format the boss kill count as a plain number with thousand separators.
    /// </summary>
    public static SessionMetricValue BossesKilled(int bossKillCount)
    {
        var display = bossKillCount.ToString("N0", CultureInfo.InvariantCulture);
        return new SessionMetricValue("bosses-killed", display);
    }

    /// <summary>
    /// Format the death count as a plain number with thousand separators.
    /// </summary>
    public static SessionMetricValue Deaths(int deaths)
    {
        var display = deaths.ToString("N0", CultureInfo.InvariantCulture);
        return new SessionMetricValue("deaths", display);
    }

    /// <summary>
    /// Format the time elapsed in the current zone. Sub-minute times render as
    /// <c>M:SS</c> (or <c>0:SS</c> under 60 seconds); hour+ times render as
    /// <c>Hh Mm</c>.
    /// </summary>
    public static SessionMetricValue TimeInZone(TimeSpan zoneElapsed)
    {
        return new SessionMetricValue("time-in-zone", FormatTimeSpan(zoneElapsed));
    }

    /// <summary>
    /// Derive the average map clear time from maps-per-hour. Returns an em-dash
    /// when there is insufficient data (≤ 0.01 mph). Otherwise computes
    /// <c>3600 / MapsPerHour</c> seconds and formats as <c>M:SS</c> or
    /// <c>Hh Mm</c>.
    /// </summary>
    public static SessionMetricValue AvgMapClearTime(float mapsPerHour)
    {
        if (mapsPerHour <= 0.01f)
            return new SessionMetricValue("avg-map-clear-time", "\u2014");

        var avgSec = 3600.0 / mapsPerHour;
        var ts = TimeSpan.FromSeconds(avgSec);
        return new SessionMetricValue("avg-map-clear-time", FormatTimeSpan(ts));
    }

    /// <summary>
    /// Shared time-span formatter used by <see cref="TimeInZone"/> and
    /// <see cref="AvgMapClearTime"/>. Rounds to the nearest second, then formats:
    /// <list type="bullet">
    ///   <item>≥ 1 hour → <c>Hh Mm</c> (e.g., "1h 5m", "2h 30m")</item>
    ///   <item>&lt; 60 minutes → <c>M:SS</c> (e.g., "0:45", "5:03", "12:34")</item>
    /// </list>
    /// </summary>
    private static string FormatTimeSpan(TimeSpan ts)
    {
        // Round to the nearest second for display
        ts = TimeSpan.FromSeconds(Math.Round(ts.TotalSeconds));

        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return $"{hours}h {minutes}m";
        }

        var totalMinutes = (int)ts.TotalMinutes;
        var seconds = ts.Seconds;
        return $"{totalMinutes}:{seconds:D2}";
    }
}