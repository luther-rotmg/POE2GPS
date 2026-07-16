using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests.Codex;

public class SessionTrackerCodexEmitTests
{
    [Fact]
    public void LevelUp_firstUpdateNeverEmits()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        t.Update(areaHash: 1, "ZoneA", areaLevel: 70, playerLevel: 42, hpPct: 100f, nowTicks: 0, excludeTowns: true, isTown: false);

        Assert.Empty(events);
    }

    [Fact]
    public void LevelUp_emitsOnlyOnIncrease()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        t.Update(1, "ZoneA", 70, 42, 100f, 0, true, false);   // first call: sets _lastPlayerLevel=42, no emit
        t.Update(1, "ZoneA", 70, 42, 100f, 0, true, false);   // same level: no emit
        t.Update(1, "ZoneA", 70, 43, 100f, 0, true, false);   // increase: emit
        t.Update(1, "ZoneA", 70, 43, 100f, 0, true, false);   // same level: no emit

        var lvl = Assert.Single(events.OfType<LevelUpEvent>());
        Assert.Equal(43, lvl.Level);
        Assert.Equal("ZoneA", lvl.Zone);
        Assert.True(lvl.Ts > 0);
    }

    [Fact]
    public void LevelUp_multiIncreaseEachEmits()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        t.Update(1, "ZoneA", 70, 42, 100f, 0, true, false);   // set baseline
        t.Update(1, "ZoneA", 70, 43, 100f, 0, true, false);   // first increase
        t.Update(1, "ZoneA", 70, 44, 100f, 0, true, false);   // second increase

        var lvls = events.OfType<LevelUpEvent>().ToList();
        Assert.Equal(2, lvls.Count);
        Assert.Equal(43, lvls[0].Level);
        Assert.Equal(44, lvls[1].Level);
    }

    [Fact]
    public void LevelUp_decreaseDoesNotEmit()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        t.Update(1, "ZoneA", 70, 42, 100f, 0, true, false);   // set baseline
        t.Update(1, "ZoneA", 70, 41, 100f, 0, true, false);   // decrease: no emit

        Assert.Empty(events);
    }

    [Fact]
    public void Death_emitsOnHpZeroEdge()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        // Establish zone and set _hpObservedAboveZero
        t.Update(areaHash: 1, "ZoneA", areaLevel: 70, playerLevel: 80, hpPct: 100f, nowTicks: 1000, excludeTowns: true, isTown: false);
        // Death: hp drops to 0
        t.Update(areaHash: 1, "ZoneA", areaLevel: 70, playerLevel: 80, hpPct: 0f, nowTicks: 2000, excludeTowns: true, isTown: false);

        var death = Assert.Single(events.OfType<DeathEvent>());
        Assert.Equal(70, death.AreaLevel);
        Assert.Equal(80, death.PlayerLevel);
        Assert.Equal("ZoneA", death.Zone);
        Assert.True(death.Ts > 0);
    }

    [Fact]
    public void Death_zoneChangeResetSuppressesFirstZero()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        // Zone A: establish _hpObservedAboveZero
        t.Update(areaHash: 1, "ZoneA", 70, 80, 100f, 1000, true, false);
        // Zone B: zone change resets _hpObservedAboveZero, hp=0 should NOT trigger death
        t.Update(areaHash: 2, "ZoneB", 71, 80, 0f, 2000, true, false);

        Assert.Empty(events.OfType<DeathEvent>());
    }

    [Fact]
    public void Death_repeatedZeroWithoutRespawnDoesNotDoubleEmit()
    {
        var events = new List<CodexEvent>();
        var t = new SessionTracker();
        t.CodexEmit += e => events.Add(e);

        // Establish zone and _hpObservedAboveZero
        t.Update(areaHash: 1, "ZoneA", 70, 80, 100f, 1000, true, false);
        // First death
        t.Update(areaHash: 1, "ZoneA", 70, 80, 0f, 2000, true, false);
        // Second zero tick: _awaitingRespawn is true, so no death
        t.Update(areaHash: 1, "ZoneA", 70, 80, 0f, 3000, true, false);

        Assert.Single(events.OfType<DeathEvent>());
    }

    [Fact]
    public void NoSubscriber_doesNotThrow()
    {
        var t = new SessionTracker();

        // Level-up sequence (no subscriber)
        t.Update(areaHash: 1, "ZoneA", 70, 42, 100f, 0, true, false);
        t.Update(areaHash: 1, "ZoneA", 70, 43, 100f, 0, true, false);

        // Death sequence (no subscriber)
        t.Update(areaHash: 1, "ZoneA", 70, 80, 100f, 1000, true, false);
        // No exception expected on the next line
        var _ = t.Update(areaHash: 1, "ZoneA", 70, 80, 0f, 2000, true, false);
    }
}