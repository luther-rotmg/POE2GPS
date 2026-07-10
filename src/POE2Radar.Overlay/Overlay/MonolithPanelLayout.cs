using System.Collections.Generic;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Pure-logic helper for the nearby-monolith reward panel. Extracted from
/// <c>OverlayRenderer.DrawMonolithPanel</c> so the click-to-collapse gate can be unit-tested
/// without a Direct2D render target. The predicate here MUST stay byte-identical to the
/// per-reward gate inside <c>DrawMonolithPanel</c> (<c>r.Ex &gt; 0</c> and <c>shown &lt; 3</c>
/// per monolith) — the two live side-by-side so a divergence trips the test immediately.
/// </summary>
public static class MonolithPanelLayout
{
    /// <summary>
    /// Number of reward rows the monolith panel would draw for <paramref name="list"/>.
    /// Returns 0 when <paramref name="collapsed"/> is true (title row only). The caller is
    /// responsible for the pre-sort / cap-to-6 on <paramref name="list"/> — this helper
    /// preserves whatever ordering it receives (see <c>ctx.MonolithsTop</c> pre-sort in
    /// <c>RadarApp</c>).
    /// </summary>
    public static int CountVisibleRewardRows(IReadOnlyList<MonolithMarker> list, bool collapsed)
    {
        if (collapsed) return 0;
        var total = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var shown = 0;
            var rewards = list[i].Rewards;
            for (var j = 0; j < rewards.Count; j++)
            {
                if (rewards[j].Ex <= 0) continue;
                if (shown >= 3) break;
                shown++;
            }
            total += shown;
        }
        return total;
    }
}
