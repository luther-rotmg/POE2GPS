using System;
using System.Collections.Generic;
using POE2Radar.Core.Campaign.Guide;
using POE2Radar.Core.Game;
using Xunit;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Tests.Core;

public class WorldStateAdapterTests
{
    private sealed class FakeInventory
    {
        public List<Poe2Live.InventoryItem> Items { get; } = new();
        public IReadOnlyList<Poe2Live.InventoryItem> Read(nint _) => Items;
    }

    private static (WorldStateAdapter adapter, FakeInventory inv) Build()
    {
        var inv = new FakeInventory();
        // Uses the internal test-seam ctor so the adapter never needs a Poe2Live bound to a live process.
        var adapter = new WorldStateAdapter(inv.Read);
        return (adapter, inv);
    }

    private static Poe2Live.EntityDot Monster(uint id, string metadata,
        int hpCur, int hpMax, Vector2 grid = default) =>
        new(id, (nint)id, grid, default, Poe2Live.EntityCategory.Monster, metadata,
            hpCur, hpMax, /*Poi*/ false, /*Reaction*/ 0, Poe2Live.Rarity.Normal, /*Opened*/ false);

    // ─── STUBS: six v0.22-pending signals ──────────────────────────────────────

    [Fact]
    public void QuestFlagSatisfied_is_always_false_in_v021()
    {
        var (a, _) = Build();
        Assert.False(((IWorldState)a).QuestFlagSatisfied(new Pattern("A6Q1_AnyFlag")));
    }

    [Fact]
    public void WaypointPulsed_is_always_false_in_v021()
    {
        var (a, _) = Build();
        Assert.False(((IWorldState)a).WaypointPulsed());
    }

    [Fact]
    public void SatisfiedFlagCount_is_always_zero_in_v021()
    {
        var (a, _) = Build();
        Assert.Equal(0, ((IWorldState)a).SatisfiedFlagCount(new[]
        {
            new Pattern("flag_one"),
            new Pattern("flag_two"),
        }));
    }

    [Fact]
    public void TalkProgress_is_always_zero_in_v021()
    {
        var (a, _) = Build();
        var m = new EntityMatcher(new Pattern("Renly"));
        Assert.Equal(0, ((IWorldState)a).TalkProgress(new[] { m }));
    }

    [Fact]
    public void InteractProgress_is_always_zero_in_v021()
    {
        var (a, _) = Build();
        var m = new EntityMatcher(new Pattern("Shrine"), MatchKind.Path);
        Assert.Equal(0, ((IWorldState)a).InteractProgress(new[] { m }));
    }

    // Sixth stub is the quest-inventory branch of LootSatisfied — reached whenever the player-inventory
    // walk misses. In v0.21 the branch returns false; v0.22's bucket walk unstubs it. Assertion: an
    // ItemMatcher whose name is nowhere in the player inventory falls through the stub and returns false.
    [Fact]
    public void LootSatisfied_quest_inventory_stub_returns_false_on_player_inventory_miss()
    {
        var (a, _) = Build();
        a.Refresh(0, 0, "G1", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        var m = new ItemMatcher(new Pattern("Blackscale Pact"), 1);
        Assert.False(((IWorldState)a).LootSatisfied(new[] { m }));
    }

    // ─── LIVE: InAreaSatisfied ─────────────────────────────────────────────────

    [Fact]
    public void InAreaSatisfied_matches_last_refreshed_area_code_case_insensitively()
    {
        var (a, _) = Build();
        a.Refresh(0, 0, "G1_1_1", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        IWorldState ws = a;
        Assert.True(ws.InAreaSatisfied(new Pattern("G1_1_1")));
        Assert.True(ws.InAreaSatisfied(new Pattern("g1_1_1")));
        Assert.False(ws.InAreaSatisfied(new Pattern("G2_1_1")));
    }

    [Fact]
    public void InAreaSatisfied_honors_regex_pattern()
    {
        var (a, _) = Build();
        a.Refresh(0, 0, "G1_1_2", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        Assert.True(((IWorldState)a).InAreaSatisfied(new Pattern("^g1_1_\\d$", Regex: true)));
    }

    [Fact]
    public void CurrentAreaCode_exposes_last_refresh_value()
    {
        var (a, _) = Build();
        a.Refresh(0, 0, "G1_1_2", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        Assert.Equal("G1_1_2", a.CurrentAreaCode);
    }

    // ─── LIVE: ProximitySatisfied ──────────────────────────────────────────────

    [Fact]
    public void ProximitySatisfied_true_when_matching_entity_is_within_radius()
    {
        var (a, _) = Build();
        var entities = new List<Poe2Live.EntityDot>
        {
            Monster(1, "Metadata/NPC/Renly",           100, 100, new Vector2(103, 104)), // dist 5
            Monster(2, "Metadata/Monsters/Skeletons/A", 100, 100, new Vector2(500, 500)), // far
        };
        a.Refresh(0, 0, "G1", new Vector2(100, 100), entities);
        var renly = new EntityMatcher(new Pattern("Metadata/NPC/Renly"), MatchKind.Path);
        Assert.True(((IWorldState)a).ProximitySatisfied(new[] { renly }, null, 10f));
        Assert.False(((IWorldState)a).ProximitySatisfied(new[] { renly }, null, 3f));
    }

    [Fact]
    public void ProximitySatisfied_false_when_entity_list_null_or_empty()
    {
        var (a, _) = Build();
        a.Refresh(0, 0, "G1", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        Assert.False(((IWorldState)a).ProximitySatisfied(null, null, 50f));
        Assert.False(((IWorldState)a).ProximitySatisfied(Array.Empty<EntityMatcher>(), null, 50f));
    }

    // ─── LIVE: KillProgress ────────────────────────────────────────────────────

    [Fact]
    public void KillProgress_counts_dead_monsters_matching_any_entity_matcher()
    {
        var (a, _) = Build();
        var pat = new EntityMatcher(new Pattern("Metadata/Monsters/Undead/UndeadA"), MatchKind.Path);
        var entities = new List<Poe2Live.EntityDot>
        {
            Monster(1, "Metadata/Monsters/Undead/UndeadA",  hpCur: 0,  hpMax: 100), // dead UndeadA
            Monster(2, "Metadata/Monsters/Undead/UndeadA",  hpCur: 50, hpMax: 100), // alive UndeadA
            Monster(3, "Metadata/Monsters/Undead/UndeadA",  hpCur: 0,  hpMax: 200), // dead UndeadA
            Monster(4, "Metadata/Monsters/Skeletons/SkelA", hpCur: 0,  hpMax: 100), // dead but no match
        };
        a.Refresh(0, 0, "G1", Vector2.Zero, entities);
        Assert.Equal(2, ((IWorldState)a).KillProgress(new[] { pat }));
    }

    [Fact]
    public void KillProgress_excludes_non_monster_categories_even_when_dead()
    {
        var (a, _) = Build();
        // A "dead" chest (HpMax=0 -> IsAlive true actually) plus a proper dead monster.
        var monster = Monster(1, "Metadata/Monsters/Undead/UndeadA", 0, 100);
        var chest = new Poe2Live.EntityDot(2, 2, default, default,
            Poe2Live.EntityCategory.Chest, "Metadata/Monsters/Undead/UndeadA",
            HpCur: 0, HpMax: 100, Poi: false, Reaction: 0, Rarity: Poe2Live.Rarity.Normal, Opened: true);
        a.Refresh(0, 0, "G1", Vector2.Zero, new[] { monster, chest });
        var pat = new EntityMatcher(new Pattern("Metadata/Monsters/Undead/UndeadA"), MatchKind.Path);
        Assert.Equal(1, ((IWorldState)a).KillProgress(new[] { pat }));
    }

    // ─── LIVE: LootSatisfied (player inventory) ────────────────────────────────

    [Fact]
    public void LootSatisfied_true_when_matcher_count_met_in_player_inventory()
    {
        var (a, inv) = Build();
        a.Refresh(0, 0, "G1", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        inv.Items.Add(new Poe2Live.InventoryItem("Blackscale Pact", "Normal", true, 1, Array.Empty<Poe2Live.RawAffix>()));
        inv.Items.Add(new Poe2Live.InventoryItem("Blackscale Pact", "Normal", true, 1, Array.Empty<Poe2Live.RawAffix>()));

        var need1 = new ItemMatcher(new Pattern("Blackscale Pact"), 1);
        var need2 = new ItemMatcher(new Pattern("Blackscale Pact"), 2);
        var need3 = new ItemMatcher(new Pattern("Blackscale Pact"), 3);
        Assert.True(((IWorldState)a).LootSatisfied(new[] { need1 }));
        Assert.True(((IWorldState)a).LootSatisfied(new[] { need2 }));
        // Count above what's held → falls to quest-inventory stub → false.
        Assert.False(((IWorldState)a).LootSatisfied(new[] { need3 }));
    }

    [Fact]
    public void LootSatisfied_all_matchers_must_be_satisfied()
    {
        var (a, inv) = Build();
        a.Refresh(0, 0, "G1", Vector2.Zero, Array.Empty<Poe2Live.EntityDot>());
        inv.Items.Add(new Poe2Live.InventoryItem("Blackscale Pact", "Normal", true, 1, Array.Empty<Poe2Live.RawAffix>()));

        var pact = new ItemMatcher(new Pattern("Blackscale Pact"), 1);
        var medallion = new ItemMatcher(new Pattern("Medallion"), 1);
        Assert.False(((IWorldState)a).LootSatisfied(new[] { pact, medallion }));
    }

    // ─── Empty-snapshot construction + toggle-mid-session ──────────────────────

    [Fact]
    public void Adapter_before_first_refresh_is_safe_for_all_reads()
    {
        var (a, _) = Build();
        IWorldState ws = a;
        Assert.Equal("", a.CurrentAreaCode);
        Assert.False(ws.InAreaSatisfied(new Pattern("G1_1_1")));
        var em = new EntityMatcher(new Pattern("Metadata/Any"), MatchKind.Path);
        Assert.False(ws.ProximitySatisfied(new[] { em }, null, 10f));
        Assert.Equal(0, ws.KillProgress(new[] { em }));
        Assert.False(ws.LootSatisfied(new[] { new ItemMatcher(new Pattern("X")) }));
    }

    // Runtime-toggle proof: adapter is constructed while the feature is off (no Refresh has run),
    // then a Refresh mid-session lights up the live signals within one call. Task 5 pairs this with
    // its CampaignReconcile call-site gate; STATE's contribution here is that the adapter never
    // caches "off" — the very next Refresh takes effect immediately.
    [Fact]
    public void Refresh_after_construction_lights_up_live_signals_within_one_call()
    {
        var (a, _) = Build();
        IWorldState ws = a;
        // Pre-refresh: nothing satisfied.
        Assert.False(ws.InAreaSatisfied(new Pattern("G1_1_1")));

        a.Refresh(0, 0, "G1_1_1", new Vector2(50, 50), Array.Empty<Poe2Live.EntityDot>());
        Assert.True(ws.InAreaSatisfied(new Pattern("G1_1_1")));
    }
}
