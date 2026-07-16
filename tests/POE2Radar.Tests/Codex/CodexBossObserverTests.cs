using System;
using System.Collections.Generic;
using POE2Radar.Core.Codex;
using POE2Radar.Core.Game;
using POE2Radar.Core.Session;
using Poe2Live = POE2Radar.Core.Game.Poe2Live;
using Xunit;

namespace POE2Radar.Tests.Codex;

public class CodexBossObserverTests
{
    private static Poe2Live.EntityDot UniqueAlive(uint id, string metadata = "")
        => new Poe2Live.EntityDot(
            id, (nint)(0x1000 + id), System.Numerics.Vector2.Zero, default(POE2Radar.Core.Game.Vector3),
            Poe2Live.EntityCategory.Monster, metadata,
            HpCur: 100, HpMax: 100, Poi: false, Reaction: 0,
            Rarity: Poe2Live.Rarity.Unique, Opened: false);

    private static Poe2Live.EntityDot UniqueDead(uint id, string metadata = "")
        => new Poe2Live.EntityDot(
            id, (nint)(0x1000 + id), System.Numerics.Vector2.Zero, default(POE2Radar.Core.Game.Vector3),
            Poe2Live.EntityCategory.Monster, metadata,
            HpCur: 0, HpMax: 100, Poi: false, Reaction: 0,
            Rarity: Poe2Live.Rarity.Unique, Opened: false);

    private static Poe2Live.EntityDot NormalAlive(uint id)
        => new Poe2Live.EntityDot(
            id, (nint)(0x1000 + id), System.Numerics.Vector2.Zero, default(POE2Radar.Core.Game.Vector3),
            Poe2Live.EntityCategory.Monster, "",
            HpCur: 100, HpMax: 100, Poi: false, Reaction: 0,
            Rarity: Poe2Live.Rarity.Normal, Opened: false);

    private static Poe2Live.EntityDot NormalDead(uint id)
        => new Poe2Live.EntityDot(
            id, (nint)(0x1000 + id), System.Numerics.Vector2.Zero, default(POE2Radar.Core.Game.Vector3),
            Poe2Live.EntityCategory.Monster, "",
            HpCur: 0, HpMax: 100, Poi: false, Reaction: 0,
            Rarity: Poe2Live.Rarity.Normal, Opened: false);

    private static BossEncounterCatalog.EncounterEntry Entry(string key, string label)
        => new BossEncounterCatalog.EncounterEntry(
            Key: key, Label: label, MatchMetadata: "",
            ZoneCodes: Array.Empty<string>(), Tier: "", Category: "",
            DamageTypes: default, OneShots: Array.Empty<string>(),
            Overcap: new Dictionary<string, int>(), FlaskNotes: "",
            Phases: Array.Empty<BossEncounterCatalog.PhaseEntry>());

    private static readonly long Ts = 1000000;

    [Fact]
    public void NonUnique_notEmitted()
    {
        var emitted = new List<CodexEvent>();
        var obs = new CodexBossObserver(
            _ => null, _ => null, ev => emitted.Add(ev));

        obs.ObserveEntityTick(NormalAlive(1), "MapMesa", Ts);
        obs.ObserveEntityTick(NormalDead(1), "MapMesa", Ts);

        Assert.Empty(emitted);
    }

    [Fact]
    public void UniqueWithNoCatalogHit_notEmitted()
    {
        var emitted = new List<CodexEvent>();
        var obs = new CodexBossObserver(
            _ => null, _ => null, ev => emitted.Add(ev));

        obs.ObserveEntityTick(UniqueAlive(1), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(1), "MapMesa", Ts);

        Assert.Empty(emitted);
    }

    [Fact]
    public void UniqueWithMetadataHit_emitsBossKillEvent()
    {
        var emitted = new List<CodexEvent>();
        var entry = Entry("arbiter", "The Arbiter of Ash");
        var obs = new CodexBossObserver(
            _ => entry, _ => null, ev => emitted.Add(ev));

        obs.ObserveEntityTick(UniqueAlive(1, "metadata/monsters/atlasbosses/demonclawboss/"), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(1, "metadata/monsters/atlasbosses/demonclawboss/"), "MapMesa", Ts);

        var ev = Assert.Single(emitted);
        var boss = Assert.IsType<BossKillEvent>(ev);
        Assert.Equal("arbiter", boss.BossKey);
        Assert.Equal("The Arbiter of Ash", boss.BossLabel);
        Assert.Equal("MapMesa", boss.Zone);
        Assert.Equal(Ts, boss.Ts);
    }

    [Fact]
    public void UniqueWithZoneOnlyHit_emitsFromZoneEntry()
    {
        var emitted = new List<CodexEvent>();
        var entry = Entry("xesht", "Xesht");
        var obs = new CodexBossObserver(
            _ => null, _ => entry, ev => emitted.Add(ev));

        obs.ObserveEntityTick(UniqueAlive(1), "MapUberBoss_Xesht", Ts);
        obs.ObserveEntityTick(UniqueDead(1), "MapUberBoss_Xesht", Ts);

        var ev = Assert.Single(emitted);
        var boss = Assert.IsType<BossKillEvent>(ev);
        Assert.Equal("xesht", boss.BossKey);
        Assert.Equal("Xesht", boss.BossLabel);
        Assert.Equal("MapUberBoss_Xesht", boss.Zone);
    }

    [Fact]
    public void SeenDeadFirst_neverEmits()
    {
        var emitted = new List<CodexEvent>();
        var entry = Entry("arbiter", "The Arbiter of Ash");
        var obs = new CodexBossObserver(
            _ => entry, _ => null, ev => emitted.Add(ev));

        // First tick: entity is already dead (never seen alive)
        obs.ObserveEntityTick(UniqueDead(1), "MapMesa", Ts);

        Assert.Empty(emitted);
    }

    [Fact]
    public void DoubleDead_emitsOnce()
    {
        var emitted = new List<CodexEvent>();
        var entry = Entry("arbiter", "The Arbiter of Ash");
        var obs = new CodexBossObserver(
            _ => entry, _ => null, ev => emitted.Add(ev));

        obs.ObserveEntityTick(UniqueAlive(1), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(1), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(1), "MapMesa", Ts + 1); // second dead tick

        Assert.Single(emitted);
    }

    [Fact]
    public void TwoDistinctBossIds_bothEmit()
    {
        var emitted = new List<CodexEvent>();
        var entry1 = Entry("arbiter", "The Arbiter of Ash");
        var entry2 = Entry("xesht", "Xesht");
        var obs = new CodexBossObserver(
            m => m.Contains("demonclaw") ? entry1 : m.Contains("xesht") ? entry2 : null,
            _ => null, ev => emitted.Add(ev));

        obs.ObserveEntityTick(UniqueAlive(1, "demonclaw/"), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueAlive(2, "xesht/"), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(1, "demonclaw/"), "MapMesa", Ts);
        obs.ObserveEntityTick(UniqueDead(2, "xesht/"), "MapMesa", Ts + 1);

        Assert.Equal(2, emitted.Count);
        var boss1 = Assert.IsType<BossKillEvent>(emitted[0]);
        var boss2 = Assert.IsType<BossKillEvent>(emitted[1]);
        Assert.Equal("arbiter", boss1.BossKey);
        Assert.Equal("xesht", boss2.BossKey);
    }

    [Fact]
    public void MetadataHitTakesPriorityOverZoneHit()
    {
        var emitted = new List<CodexEvent>();
        var metaEntry = Entry("arbiter", "The Arbiter of Ash");
        var zoneEntry = Entry("xesht", "Xesht");
        var obs = new CodexBossObserver(
            _ => metaEntry, _ => zoneEntry, ev => emitted.Add(ev));

        // Both lookups return non-null. Metadata should win.
        obs.ObserveEntityTick(UniqueAlive(1, "demonclaw/"), "MapUberBoss_Xesht", Ts);
        obs.ObserveEntityTick(UniqueDead(1, "demonclaw/"), "MapUberBoss_Xesht", Ts);

        var ev = Assert.Single(emitted);
        var boss = Assert.IsType<BossKillEvent>(ev);
        Assert.Equal("arbiter", boss.BossKey);
        Assert.Equal("The Arbiter of Ash", boss.BossLabel);
    }

    [Fact]
    public void NoSink_throwsArgumentNullException()
    {
        // ReSharper disable once AssignNullToNotNullAttribute
        Assert.Throws<ArgumentNullException>(() => new CodexBossObserver(
            _ => null, _ => null, null!));
    }
}