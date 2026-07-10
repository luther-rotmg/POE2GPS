using System;
using System.Globalization;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Pure static formatter for the XP/hour Session HUD row (Threshold — THR-XP-RENDER).
///
/// Kept dependency-free so the test project can cover the humanization thresholds and
/// the split-vs-single-line branch without pulling Direct2D. All rendering (brush,
/// DrawText, panel geometry) stays in <see cref="OverlayRenderer.DrawSessionHud"/> —
/// this file only produces the pre-formatted strings the renderer paints.
/// </summary>
public static class SessionHudXpFormatter
{
    // ── Humanization thresholds locked by spec §4.4. Each SI-tier splits into a
    //    2-decimal band (single leading digit) and an integer band (multi-digit),
    //    so 9_999 renders as "9.99K" (not "10.00K"), 10_000 as "10K", 999_999_999
    //    as "999M" (not "1000.00M"). Decimals are TRUNCATED, never rounded — a
    //    round-up on 9_999 would overflow into the next tier's format and drift
    //    the humanization thresholds:
    //      <1K       → raw digits
    //      [1K, 10K) → "1.24K" (two decimals, truncated)
    //      <1M       → "245K"  (integer)
    //      <10M      → "1.24M" (two decimals, truncated)
    //      <1B       → "999M"  (integer)
    //      <10B      → "2.10B" (two decimals, truncated)
    //      else      → integer B ──
    public static string Humanize(long value)
    {
        if (value < 0) value = 0;
        if (value < 1_000L)             return value.ToString(CultureInfo.InvariantCulture);
        if (value < 10_000L)            return (Math.Floor(value / 10d) / 100d).ToString("0.00", CultureInfo.InvariantCulture) + "K";
        if (value < 1_000_000L)         return (value / 1_000L).ToString(CultureInfo.InvariantCulture) + "K";
        if (value < 10_000_000L)        return (Math.Floor(value / 10_000d) / 100d).ToString("0.00", CultureInfo.InvariantCulture) + "M";
        if (value < 1_000_000_000L)     return (value / 1_000_000L).ToString(CultureInfo.InvariantCulture) + "M";
        if (value < 10_000_000_000L)    return (Math.Floor(value / 10_000_000d) / 100d).ToString("0.00", CultureInfo.InvariantCulture) + "B";
        return (value / 1_000_000_000L).ToString(CultureInfo.InvariantCulture) + "B";
    }

    /// <summary>
    /// Build the XP row strings. Single line when the ring has filled AND the resolver
    /// yields a TTL; two lines while the ring is still filling (renderer paints them
    /// as consecutive rows so the panel stays honest during the first window of
    /// samples). Returns <c>noData=true</c> when <paramref name="xpPerHour"/> is
    /// non-positive — the caller paints the row in the shared yellow "no data" tint
    /// already used by the deaths row.
    ///
    /// <paramref name="timeToNextResolver"/> receives (currentLevel, currentXp,
    /// xpPerHour) and returns null when the caller is at max level or the rate is
    /// non-positive. Passing the static method group of
    /// <c>PoE2XpCurveLoader.TimeToNextLevel</c> keeps the call site allocation-free.
    /// </summary>
    public static (string primary, string? secondary, bool noData) FormatXpRow(
        float xpPerHour,
        int   currentLevel,
        long  currentXp,
        bool  ringFilling,
        Func<int, long, float, TimeSpan?> timeToNextResolver)
    {
        if (xpPerHour <= 0f)
            return ("XP/hr    --", null, true);

        var rateText = Humanize((long)xpPerHour);
        var ttl      = timeToNextResolver(currentLevel, currentXp, xpPerHour);
        var ttlText  = ttl.HasValue ? FormatTimeToNext(ttl.Value, currentLevel + 1) : null;

        if (ttlText == null)
            return ($"XP/hr    {rateText}", null, false);

        if (ringFilling)
            return ($"XP/hr    {rateText}", ttlText, false);

        return ($"XP/hr    {rateText}  {ttlText}", null, false);
    }

    private static string FormatTimeToNext(TimeSpan span, int nextLevel)
    {
        // Clamp negative spans to zero minutes so the row never renders a "-3m to
        // L86" oddity if the resolver ever hands back a stale clock delta.
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalMinutes < 60)
            return $"({(int)span.TotalMinutes}m to L{nextLevel})";

        if (span.TotalHours < 24)
            return $"({(int)span.TotalHours}h {span.Minutes}m to L{nextLevel})";

        return $"({(int)span.TotalDays}d {span.Hours}h to L{nextLevel})";
    }
}

/// <summary>
/// Gate helper for the zero-cost-when-off contract (Threshold spec §2): the
/// per-refresh <c>_live.PlayerExperience(localPlayer)</c> read in
/// <c>RadarApp.WorldTick</c> is NOT invoked unless BOTH the master HUD toggle
/// and the XP-rate row toggle are on. Extracted as a static extension so the
/// spy test can exercise the exact same predicate the production call site
/// uses — zero allocations, zero closures, one grep.
/// </summary>
public static class SessionHudSettingsExt
{
    public static bool ShouldReadXpRate(this SessionHudSettings hud)
        => hud.Enabled && hud.ShowXpRate;
}
