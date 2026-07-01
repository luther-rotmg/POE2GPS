using POE2Radar.Core.Session;

public class SessionTrackerTests
{
    // Helper: ticks representing N seconds from a fixed origin
    private static long T(double seconds) =>
        (long)(seconds * TimeSpan.TicksPerSecond);

    // Helper: call Update with default/pass-through values for fields under test.
    // hpPct defaults to 100f (alive) — HpPct is a [0,100] percentage, not [0,1].
    private static SessionStats Step(SessionTracker t,
        uint areaHash = 1, string areaCode = "G1_1", int areaLevel = 1,
        float hpPct = 100f, long nowTicks = 0,
        bool excludeTowns = false, bool isTown = false)
        => t.Update(areaHash, areaCode, areaLevel, playerLevel: 0, hpPct, nowTicks, excludeTowns, isTown);

    [Fact]
    public void FirstUpdate_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        var s = Step(t, areaHash: 1, nowTicks: T(0));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void ZoneChange_IncrementsZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, nowTicks: T(10));
        Assert.Equal(1, s.ZonesEntered);
    }

    [Fact]
    public void SameHash_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 1, nowTicks: T(5));
        var s = Step(t, areaHash: 1, nowTicks: T(10));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void TwoZoneChanges_CountsTwo()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(10));
        var s = Step(t, areaHash: 3, nowTicks: T(20));
        Assert.Equal(2, s.ZonesEntered);
    }

    [Fact]
    public void TownEntry_ExcludeEnabled_DoesNotCount()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
                     excludeTowns: true, nowTicks: T(10));
        Assert.Equal(0, s.ZonesEntered);
    }

    [Fact]
    public void TownEntry_ExcludeDisabled_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        var s = Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
                     excludeTowns: false, nowTicks: T(10));
        Assert.Equal(1, s.ZonesEntered);
    }

    [Fact]
    public void NonTownAfterTown_ExcludeEnabled_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, areaCode: "G1_town", isTown: true,
             excludeTowns: true, nowTicks: T(10));
        var s = Step(t, areaHash: 3, areaCode: "G1_1", isTown: false,
                     excludeTowns: true, nowTicks: T(20));
        Assert.Equal(1, s.ZonesEntered);
    }

    [Fact]
    public void DeathFlashOnLoad_IsIgnored()
    {
        // HP is 0 on first update (zone load), then recovers — must NOT count as death
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        var s = Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Assert.Equal(0, s.Deaths);
    }

    [Fact]
    public void Death_AfterObservedAlive_Counts()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0)); // load flash — ignored
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1)); // alive observed
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(3)); // still dead
        Assert.Equal(1, s.Deaths);
    }

    [Fact]
    public void BackToBackDeaths_RequireRespawnBetween()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death 1
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(3)); // still 0 — no second count
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(4));
        Assert.Equal(1, s.Deaths);
    }

    [Fact]
    public void TwoDeaths_AfterRespawnBetween_CountsTwo()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death 1
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(3)); // respawn
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(4)); // death 2
        var s = Step(t, areaHash: 1, hpPct: 0f, nowTicks: T(5));
        Assert.Equal(2, s.Deaths);
    }

    [Fact]
    public void ZoneChange_ResetsPerZoneDeaths_AndDeathFlashGuard()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death in zone 1
        Step(t, areaHash: 2, hpPct: 0f,   nowTicks: T(3)); // zone change; load-flash 0
        var s = Step(t, areaHash: 2, hpPct: 100f, nowTicks: T(4)); // alive
        Assert.Equal(1, s.Deaths);          // session total preserved
        Assert.Equal(0, s.DeathsThisZone);  // per-zone reset
    }

    [Fact]
    public void DeathFlashInNewZone_AfterZoneChange_IsIgnored()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(0));
        Step(t, areaHash: 2, hpPct: 0f,   nowTicks: T(1)); // zone change with HP=0 flash
        var s = Step(t, areaHash: 2, hpPct: 100f, nowTicks: T(2));
        Assert.Equal(0, s.Deaths);
    }

    [Fact]
    public void ZonesPerHour_ZeroWhenUnderOneMinute()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(30));   // 30 seconds in
        var s = Step(t, areaHash: 2, nowTicks: T(59));
        Assert.Equal(0f, s.ZonesPerHour);
    }

    [Fact]
    public void ZonesPerHour_CorrectAfterOneMinute()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(60));
        var s = Step(t, areaHash: 2, nowTicks: T(60));
        // 1 zone entered at T=60 => 1 / (60/3600) = 60 zones/hr
        Assert.Equal(60f, s.ZonesPerHour, precision: 0);
    }

    [Fact]
    public void SessionElapsed_MatchesWallTime()
    {
        var t = new SessionTracker();
        Step(t, nowTicks: T(0));
        var s = Step(t, nowTicks: T(90));
        Assert.Equal(TimeSpan.FromSeconds(90), s.SessionElapsed);
    }

    [Fact]
    public void ZoneElapsed_ResetsOnZoneChange()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 1, nowTicks: T(50));
        Step(t, areaHash: 2, nowTicks: T(60)); // zone change at T=60
        var s = Step(t, areaHash: 2, nowTicks: T(70));
        Assert.Equal(TimeSpan.FromSeconds(10), s.ZoneElapsed);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(0));
        Step(t, areaHash: 1, hpPct: 100f, nowTicks: T(1));
        Step(t, areaHash: 1, hpPct: 0f,   nowTicks: T(2)); // death
        Step(t, areaHash: 2, nowTicks: T(10));             // zone
        t.Reset(T(10));
        var s = Step(t, areaHash: 2, nowTicks: T(20));
        Assert.Equal(0, s.Deaths);
        Assert.Equal(0, s.ZonesEntered);
        Assert.Equal(TimeSpan.FromSeconds(10), s.SessionElapsed);
    }

    [Fact]
    public void Reset_NextAreaHash_DoesNotIncrementZones()
    {
        var t = new SessionTracker();
        Step(t, areaHash: 1, nowTicks: T(0));
        Step(t, areaHash: 2, nowTicks: T(10));
        t.Reset(T(10));
        // After reset, areaHash=2 is the "current" hash; same-hash Update must not increment.
        var s = Step(t, areaHash: 2, nowTicks: T(20));
        Assert.Equal(0, s.ZonesEntered);
    }
}
