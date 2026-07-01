using POE2Radar.Core.Game;      // Poe2Live, Poe2Live.Rarity
using POE2Radar.Core.Session;

public class KillTrackerTests
{
    [Fact] public void Hp_to_zero_counts_one_kill_by_rarity()
    {
        var t = new KillTracker();
        t.Observe(0x100, Poe2Live.Rarity.Rare, 50, 100);   // alive
        t.Observe(0x100, Poe2Live.Rarity.Rare, 0, 100);    // died
        Assert.Equal((0,0,1,0), t.Counts);
    }
    [Fact] public void Death_counts_only_once_even_if_observed_dead_repeatedly()
    {
        var t = new KillTracker();
        t.Observe(0x1, Poe2Live.Rarity.Unique, 10, 10);
        t.Observe(0x1, Poe2Live.Rarity.Unique, 0, 10);
        t.Observe(0x1, Poe2Live.Rarity.Unique, 0, 10);     // still dead, re-observed
        Assert.Equal((0,0,0,1), t.Counts);
    }
    [Fact] public void Eviction_does_not_count_as_a_kill()
    {
        var t = new KillTracker();
        t.Observe(0x2, Poe2Live.Rarity.Magic, 30, 30);     // alive, then never seen again
        t.ClearZone();                                      // zone change drops it, uncounted
        t.Observe(0x2, Poe2Live.Rarity.Magic, 0, 30);      // a NEW entity reusing addr, first-seen dead → not a kill
        Assert.Equal((0,0,0,0), t.Counts);
    }
    [Fact] public void Non_monster_and_zero_max_ignored()
    {
        var t = new KillTracker();
        t.Observe(0x3, Poe2Live.Rarity.NonMonster, 0, 0);
        Assert.Equal((0,0,0,0), t.Counts);
    }
    [Fact] public void Reset_clears_counts_and_tracking()
    {
        var t = new KillTracker();
        t.Observe(0x4, Poe2Live.Rarity.Rare, 5, 5); t.Observe(0x4, Poe2Live.Rarity.Rare, 0, 5);
        t.Reset();
        Assert.Equal((0,0,0,0), t.Counts);
    }
}
