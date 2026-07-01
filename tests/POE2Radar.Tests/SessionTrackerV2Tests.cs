using POE2Radar.Core.Game;
using POE2Radar.Core.Session;

public class SessionTrackerV2Tests
{
    const long Hour = TimeSpan.TicksPerHour;

    [Fact] public void Xp_efficiency_is_player_minus_area_level()
    {
        var t = new SessionTracker();
        var s = t.Update(areaHash: 1, "map", areaLevel: 70, playerLevel: 74, hpPct: 100, nowTicks: 0, excludeTowns: true, isTown: false);
        Assert.Equal(4, s.XpEfficiency);
    }

    [Fact] public void Maps_per_hour_counts_only_non_town_zone_entries()
    {
        var t = new SessionTracker();
        t.Update(1, "town",  60, 70, 100, 0,      true, isTown: true);   // town: not a map
        t.Update(2, "mapA",  70, 70, 100, 0,      true, isTown: false);  // map entry 1
        var s = t.Update(3, "mapB", 70, 70, 100, Hour/2, true, isTown: false); // map entry 2 @ 0.5h
        Assert.Equal(2, s.MapZonesEntered);
        Assert.True(s.MapsPerHour > 3.9f && s.MapsPerHour < 4.1f);       // 2 / 0.5h ≈ 4
    }

    [Fact] public void Kills_flow_through_to_stats()
    {
        var t = new SessionTracker();
        t.ObserveKill(0x10, Poe2Live.Rarity.Rare, 5, 5); t.ObserveKill(0x10, Poe2Live.Rarity.Rare, 0, 5);
        var s = t.Update(1, "map", 70, 74, 100, 0, true, false);
        Assert.Equal(1, s.KillsRare);
    }

    [Fact] public void Reset_zeroes_kills_and_maps()
    {
        var t = new SessionTracker();
        t.ObserveKill(0x1, Poe2Live.Rarity.Unique, 5, 5); t.ObserveKill(0x1, Poe2Live.Rarity.Unique, 0, 5);
        t.Update(2, "mapA", 70, 74, 100, 0, true, false);
        t.Reset(0);
        var s = t.Update(3, "mapB", 70, 74, 100, 0, true, false);
        Assert.Equal(0, s.KillsUnique);
        Assert.Equal(1, s.MapZonesEntered);   // the post-reset entry
    }
}
