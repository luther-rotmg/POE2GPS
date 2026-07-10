using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Session;

// Static reader for the PoE2 cumulative XP curve.
//
// SOURCE OF TRUTH: the embedded resource
//   POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json
// already carried by Core.csproj (see RouteModel.XpCurveResource in
// Campaign/Guide/RouteModel.cs). The JSON is 100 entries indexed 0..99 where
// index (L - 1) is the cumulative XP required to REACH level L. Index 0 == 0.
//
// Do NOT bake the array into C# as a duplicate static long[]. Two sources of
// truth silently drift on any future PoE2 XP rebalance.
public static class PoE2XpCurveLoader
{
    // resource name matches RouteModel.XpCurveResource verbatim so a rename
    // lands in one grep.
    private const string XpCurveResource =
        "POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json";

    private static readonly Lazy<long[]> _cumulative =
        new(LoadCumulative, isThreadSafe: true);

    // 100-entry cumulative curve. Index 0 == L1 threshold (0).
    // Index 99 == L100 threshold (4,250,334,444). Exposed primarily for tests
    // and dashboard diagnostics; runtime consumers should prefer the helpers.
    public static long[] Cumulative => _cumulative.Value;

    // XP still needed to reach (level + 1). Returns null when the player is
    // already at max (level >= 100) or the caller passed a nonsense level.
    // Overshoot (currentXp already past the next threshold) clamps to 0
    // rather than emitting a negative delta.
    public static long? XpToNextLevel(int level, long currentXp)
    {
        if (level < 1 || level >= 100) return null;
        var arr = _cumulative.Value;
        // arr[level] is index (level+1 - 1) == XP required to reach (level+1).
        long threshold = arr[level];
        long remaining = threshold - currentXp;
        return remaining < 0 ? 0 : remaining;
    }

    // Wall-clock estimate to reach next level given a sustained xpPerHour.
    // Null when xpPerHour is non-positive OR the player is at max.
    // Callers gate on ShowXpRate before invoking; no per-tick work here.
    public static TimeSpan? TimeToNextLevel(int level, long currentXp, float xpPerHour)
    {
        if (xpPerHour <= 0f) return null;
        var remaining = XpToNextLevel(level, currentXp);
        if (remaining is null) return null;
        double hours = remaining.Value / (double)xpPerHour;
        return TimeSpan.FromHours(hours);
    }

    private static long[] LoadCumulative()
    {
        var asm = typeof(PoE2XpCurveLoader).Assembly;
        using var s = asm.GetManifestResourceStream(XpCurveResource)
            ?? throw new InvalidOperationException(
                $"embedded resource '{XpCurveResource}' not found");
        using var doc = JsonDocument.Parse(s);
        var cum = doc.RootElement.GetProperty("cumulative");
        int len = cum.GetArrayLength();
        var arr = new long[len];
        for (int i = 0; i < len; i++)
            arr[i] = cum[i].GetInt64();
        return arr;
    }
}
