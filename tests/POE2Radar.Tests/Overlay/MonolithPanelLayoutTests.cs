using System.Collections.Generic;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Overlay;
using Xunit;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// Locks the click-to-collapse gate for the nearby-monolith reward panel. The renderer's
/// per-reward predicate (<c>r.Ex &gt; 0</c> and <c>shown &lt; 3</c> per monolith) lives
/// alongside this helper so a divergence trips these tests immediately. Note: sort order
/// of the input list is intentionally NOT asserted here — the panel receives a pre-sorted,
/// cap-to-6 slice via <c>ctx.MonolithsTop</c> assembled in <c>RadarApp</c>, and the row-count
/// contract of the collapse gate is independent of that ordering.
/// </summary>
public class MonolithPanelLayoutTests
{
    static MonolithMarker Mk(params double[] rewardEx)
    {
        var rewards = new List<MonolithReward>();
        foreach (var ex in rewardEx)
            rewards.Add(new MonolithReward("r", 1, ex, 0, ""));
        return new MonolithMarker(
            Grid: new NumVec2(0, 0), Holes: 3, IsUnique: false, Collected: false,
            AnchorName: "A", BestEx: rewardEx.Length > 0 ? rewardEx[0] : 0,
            BestName: "r", Color: 0xFFFFFFFFu, Rewards: rewards);
    }

    [Fact]
    public void OverlayRenderer_DrawMonolithPanel_CollapsedStateHidesRewardRows()
    {
        var list = new List<MonolithMarker> { Mk(50, 20, 10), Mk(30, 5) };

        // Expanded: sums (Ex>0 && shown<3) rows per monolith -> 3 + 2 = 5.
        Assert.Equal(5, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));

        // Collapsed: title-only panel -- zero reward rows drawn.
        Assert.Equal(0, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: true));
    }

    [Fact]
    public void CountVisibleRewardRows_CapsAtThreeRewardsPerMonolith()
    {
        // Preserves DrawMonolithPanel's shown >= 3 cap per monolith.
        var list = new List<MonolithMarker> { Mk(50, 40, 30, 20, 10) };
        Assert.Equal(3, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));
    }

    [Fact]
    public void CountVisibleRewardRows_SkipsZeroOrNegativeEx()
    {
        // Preserves DrawMonolithPanel's r.Ex <= 0 skip.
        var list = new List<MonolithMarker> { Mk(50, 0, 10, -1, 5) };
        Assert.Equal(3, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));
    }
}
