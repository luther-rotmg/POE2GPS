using POE2Radar.Core.Game;

public class PreloadTrackerTests
{
    [Fact] public void Rare_path_alerts_after_warmup()
    {
        var t = new PreloadTracker(warmupZones: 4, commonThreshold: 0.6);
        for (int z = 0; z < 4; z++) t.ObserveZone(new[] { "common/a" });         // saturate a common path
        var res = t.ObserveZone(new[] { "common/a", "rare/boss" });               // zone 5: rare appears once
        Assert.Contains("rare/boss", res.Alerts);
        Assert.DoesNotContain("common/a", res.Alerts);   // common/a in 5/5 zones → suppressed
    }
    [Fact] public void During_warmup_everything_alerts()
    {
        var t = new PreloadTracker(warmupZones: 4, commonThreshold: 0.6);
        var res = t.ObserveZone(new[] { "x/y" });
        Assert.Contains("x/y", res.Alerts);              // no data yet → don't suppress
    }
    [Fact] public void Frequencies_exposed_for_diagnostic()
    {
        var t = new PreloadTracker(4, 0.6);
        t.ObserveZone(new[] { "p" }); t.ObserveZone(new[] { "p" });
        Assert.Equal(2, t.Snapshot()["p"].hits);
        Assert.Equal(2, t.ZonesObserved);
    }
}
