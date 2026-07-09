// v0.22 campaign-probe — PROBE-CORE verify gate.
// One PASS test per event type (12 event types) + opt-off spy + zero-allocation gate. Every test
// drives the internal test-seam ctor with a ProbeAccessors delegate bag, avoiding any bind to a
// live PoE2 process. The observer sink is a real EventWriter pointed at a per-test temp directory
// (the class is sealed, so we can't mock it — but the EmitObserver hook lets us watch every
// enqueue synchronously without touching disk).
namespace POE2Radar.Tests.Campaign.Probe;

// Task 1's tests live under POE2Radar.Tests.CampaignProbe (single-word namespace segment),
// which shadows the CampaignProbe type when we look it up from POE2Radar.Tests.Campaign.Probe.
// Placing the alias INSIDE the file-scoped namespace makes it win over the outer-namespace
// lookup that would otherwise resolve to POE2Radar.Tests.CampaignProbe. The Vector2 alias is
// disambiguation against POE2Radar.Core.Game.Vector2 (which also lives in the Game namespace).
using POE2Radar.Core.Campaign.Probe;
using POE2Radar.Core.Game;
using CampaignProbe = POE2Radar.Core.Campaign.Probe.CampaignProbe;
using Vector2 = System.Numerics.Vector2;

public sealed class CampaignProbeCoreTests : IDisposable
{
    private readonly string _baseDir;
    private readonly List<EventWriter> _writers = new();

    public CampaignProbeCoreTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "poe2gps-probe-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        foreach (var w in _writers)
        {
            try { w.DisposeAsync().AsTask().Wait(500); } catch { /* best-effort */ }
        }
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Fake state — the probe's ProbeAccessors delegate bag reads from this per-instance state
    //    keyed on the entity address the snap passes in.
    private sealed class FakeState
    {
        public long CharExp = 0;
        public List<ushort> Passives = new();
        public nint HoverEntity = 0;
        public Dictionary<nint, byte> Targetable = new();      // isTargetable byte
        public Dictionary<nint, byte> Targeted = new();        // isTargeted byte (interact edge)
        public List<nint> UiTreeElements = new();

        public ProbeAccessors Accessors() => new(
            PlayerExperience:           _ => CharExp,
            AllocatedPassiveNodeIds:    _ => Passives,
            TryReadTargetable:          TryReadTargetable,
            TryReadChestState:          (nint _, out bool o, out bool l) => { o = l = false; return false; },
            TryReadShrineUsed:          (nint _, out bool u) => { u = false; return false; },
            TryReadTransitionableState: (nint _, out short s) => { s = 0; return false; },
            TryReadTriggerableBlockage: (nint _, out bool b) => { b = false; return false; },
            TryReadQuestFlag:           (nint _, uint _, out bool v) => { v = false; return false; },
            HoveredEntityViaTracker:    _ => HoverEntity,
            WalkUiTree:                 (_, _) => UiTreeElements);

        private bool TryReadTargetable(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden)
        {
            isTargetable = Targetable.TryGetValue(entity, out var t) ? t : (byte)0;
            isHighlight = 0;
            isTargeted = Targeted.TryGetValue(entity, out var g) ? g : (byte)0;
            isHidden = 0;
            return true;
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────────────────────

    private (CampaignProbe probe, List<EventRecord> emits, FakeState state, EventWriter writer)
        BuildProbe(bool enabled = true)
    {
        var writer = new EventWriter(
            installUuid:   "11111111-1111-4111-8111-111111111111",
            bootEpochMs:   1_720_000_000_000L,
            baseDirectory: _baseDir);
        _writers.Add(writer);
        var state = new FakeState();
        long t = 1_000_000L;
        var probe = new CampaignProbe(
            isEnabledGate:     () => enabled,
            installIdProvider: () => "11111111-1111-4111-8111-111111111111",
            writer:            writer,
            accessors:         state.Accessors(),
            bootId:            "boot-abc",
            nowMs:             () => t++);
        var emits = new List<EventRecord>();
        probe.EmitObserver = emits.Add;
        return (probe, emits, state, writer);
    }

    private static Poe2Live.EntityDot Ent(uint id, string metadata, Poe2Live.EntityCategory cat,
        Vector2 grid = default, Poe2Live.Rarity rarity = Poe2Live.Rarity.NonMonster,
        int hpCur = 100, int hpMax = 100)
        => new(id, (nint)id, grid, default, cat, metadata, hpCur, hpMax,
               Poi: false, Reaction: 0, Rarity: rarity, Opened: false);

    private static CampaignProbeSnap Snap(
        string areaCode,
        string areaName,
        Vector2 player,
        IReadOnlyList<Poe2Live.EntityDot> ents,
        int areaLevel = 10,
        bool isTown = false,
        bool isHideout = false,
        int charLevel = 5,
        long xp = 100L,
        bool isAlive = true,
        IReadOnlyList<ushort>? passives = null,
        IReadOnlyList<nint>? uiRoots = null,
        string? lastDamage = null,
        nint inGameState = 0x1000,
        nint areaInstance = 0x2000,
        nint localPlayer = 0x3000,
        uint areaHash = 0xABCDEF01)
        => new(
            InGameState:              inGameState,
            AreaInstance:             areaInstance,
            LocalPlayer:              localPlayer,
            AreaCode:                 areaCode,
            AreaName:                 areaName,
            AreaHash:                 areaHash,
            AreaLevel:                areaLevel,
            IsTown:                   isTown,
            IsHideout:                isHideout,
            PlayerWorldPos:           new WorldPos(player.X, player.Y),
            CharacterLevel:           charLevel,
            CurrentXp:                xp,
            IsPlayerAlive:            isAlive,
            UiTreeRoots:              uiRoots ?? Array.Empty<nint>(),
            Entities:                 ents,
            AllocatedPassiveNodeIds:  passives ?? Array.Empty<ushort>(),
            LastDamageSourceMetadata: lastDamage);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Non-negotiable: zero-cost-when-off. Disabled probe emits nothing across 1000 ticks AND
    // performs zero managed allocations (GC.GetAllocatedBytesForCurrentThread delta == 0).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_probe_emits_nothing_and_zero_allocs_across_1000_ticks()
    {
        var (probe, emits, _, writer) = BuildProbe(enabled: false);
        var snap = Snap("G1_1", "The Riverbank", new Vector2(10, 10), Array.Empty<Poe2Live.EntityDot>());

        // Warm up JIT so the first-tick specialisation doesn't skew the sample.
        probe.Tick(snap);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) probe.Tick(snap);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Empty(emits);
        Assert.Equal(0, writer.EventsWritten);
        Assert.Equal(0, after - before);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1) zone_entered
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zone_entered_fires_on_area_code_diff_and_carries_area_metadata()
    {
        var (probe, emits, _, _) = BuildProbe();
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(5, 5), Array.Empty<Poe2Live.EntityDot>(), areaLevel: 2));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50, 50), Array.Empty<Poe2Live.EntityDot>(), areaLevel: 3));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50, 50), Array.Empty<Poe2Live.EntityDot>(), areaLevel: 3));

        var entered = emits.OfType<ZoneEnteredEvent>().ToList();
        Assert.Equal(2, entered.Count);
        Assert.Equal("Clearfell", entered[1].Envelope.AreaName);
        Assert.Equal(3, entered[1].AreaLevel);
        Assert.Equal(new WorldPos(50, 50), entered[1].PlayerWorldPos);
        Assert.False(string.IsNullOrEmpty(entered[1].AreaIdHash));
        Assert.Equal("live", entered[1].Envelope.ProbeCapability);
        Assert.Equal(1, entered[1].Envelope.SchemaVersion);
        Assert.Equal("act1", entered[1].Envelope.ActHint);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2) area_transition_used — a targeted transition entity in the previous zone, followed by a
    //    zone change, emits with the source→destination pair.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Area_transition_used_fires_after_targeted_transition_and_zone_change()
    {
        var (probe, emits, state, _) = BuildProbe();
        var transition = Ent(500, "Metadata/Transition/GateToClearfell", Poe2Live.EntityCategory.Transition,
            grid: new Vector2(10, 10));
        state.Targeted[transition.Address] = 1;

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(10, 10), new[] { transition }));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50, 50), Array.Empty<Poe2Live.EntityDot>()));

        var trans = emits.OfType<AreaTransitionUsedEvent>().ToList();
        Assert.Single(trans);
        Assert.Equal("G1_1", trans[0].SourceArea);
        Assert.Equal("G1_2", trans[0].DestinationArea);
        Assert.Equal("Metadata/Transition/GateToClearfell", trans[0].TransitionEntityMetadataPath);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3) boss_encountered — Unique-rarity monster in radius, once per area per id.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Boss_encountered_fires_once_per_unique_monster_in_radius()
    {
        var (probe, emits, _, _) = BuildProbe();
        var boss = Ent(42, "Metadata/Monsters/BossThatHits", Poe2Live.EntityCategory.Monster,
            grid: new Vector2(12, 12), rarity: Poe2Live.Rarity.Unique);
        var mook = Ent(43, "Metadata/Monsters/FilthyMook", Poe2Live.EntityCategory.Monster,
            grid: new Vector2(11, 11), rarity: Poe2Live.Rarity.Normal);

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(10, 10), new[] { boss, mook }));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(10, 10), new[] { boss, mook }));  // dedupe

        var bosses = emits.OfType<BossEncounteredEvent>().ToList();
        Assert.Single(bosses);
        Assert.Equal("Metadata/Monsters/BossThatHits", bosses[0].BossMetadataPath);
        Assert.True(bosses[0].IsFirstEncounter);
        Assert.Equal(new WorldPos(12, 12), bosses[0].BossWorldPos);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4) checkpoint_touched — checkpoint-metadata entity within interact radius, once per (area, id).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Checkpoint_touched_fires_once_per_checkpoint_within_interact_radius()
    {
        var (probe, emits, _, _) = BuildProbe();
        var checkpoint = Ent(70, "Metadata/Object/CampaignCheckpointA", Poe2Live.EntityCategory.Object,
            grid: new Vector2(5, 5));

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(5, 5), new[] { checkpoint }));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(5, 5), new[] { checkpoint }));   // dedupe

        var cps = emits.OfType<CheckpointTouchedEvent>().ToList();
        Assert.Single(cps);
        Assert.Equal("Metadata/Object/CampaignCheckpointA", cps[0].CheckpointMetadataPath);
        Assert.Equal(new WorldPos(5, 5), cps[0].WorldPos);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5) waypoint_unlocked — waypoint-metadata entity within interact radius, once per (area, id).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Waypoint_unlocked_fires_once_per_waypoint_within_interact_radius()
    {
        var (probe, emits, _, _) = BuildProbe();
        var wp = Ent(99, "Metadata/Object/WaypointClearfell", Poe2Live.EntityCategory.Object,
            grid: new Vector2(6, 6));

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(6, 6), new[] { wp }));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(6, 6), new[] { wp }));

        var wps = emits.OfType<WaypointUnlockedEvent>().ToList();
        Assert.Single(wps);
        Assert.Equal("Metadata/Object/WaypointClearfell", wps[0].WaypointEntityMetadataPath);
        Assert.Equal(new WorldPos(6, 6), wps[0].WorldPos);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6) player_death — IsPlayerAlive false edge, once per life reset.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Player_death_fires_on_alive_false_edge_and_re_fires_after_revive()
    {
        var (probe, emits, _, _) = BuildProbe();
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(1, 1), Array.Empty<Poe2Live.EntityDot>(), isAlive: true));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(1, 1), Array.Empty<Poe2Live.EntityDot>(), isAlive: false, lastDamage: "Metadata/Monsters/Killer"));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(1, 1), Array.Empty<Poe2Live.EntityDot>(), isAlive: false));  // still dead, no re-fire
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(1, 1), Array.Empty<Poe2Live.EntityDot>(), isAlive: true));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(1, 1), Array.Empty<Poe2Live.EntityDot>(), isAlive: false));

        var deaths = emits.OfType<PlayerDeathEvent>().ToList();
        Assert.Equal(2, deaths.Count);
        Assert.Equal("Metadata/Monsters/Killer", deaths[0].LastDamageSourceMetadataPath);
        Assert.Null(deaths[1].LastDamageSourceMetadataPath);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 7) level_up — CharacterLevel edge up, XP long-widened.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Level_up_fires_on_char_level_edge_up_with_widened_xp()
    {
        var (probe, emits, _, _) = BuildProbe();
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(), charLevel: 5, xp: 500L));
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(), charLevel: 6, xp: 640L));
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(), charLevel: 6, xp: 700L));

        var ups = emits.OfType<LevelUpEvent>().ToList();
        Assert.Single(ups);
        Assert.Equal(6, ups[0].NewLevel);
        Assert.Equal(640L, ups[0].XpAtLevel);
        Assert.Equal("The Riverbank", ups[0].AreaNameWhenLeveled);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 8) passive_allocated — set diff, once per new node id.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Passive_allocated_fires_once_per_newly_seen_node_id()
    {
        var (probe, emits, _, _) = BuildProbe();
        var seed = new ushort[] { 1, 2, 3 };
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(),
            charLevel: 20, passives: seed));
        Assert.Empty(emits.OfType<PassiveAllocatedEvent>());

        var grown = new ushort[] { 1, 2, 3, 4, 5 };
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(),
            charLevel: 20, passives: grown));

        var allocs = emits.OfType<PassiveAllocatedEvent>().OrderBy(e => e.NodeId).ToList();
        Assert.Equal(2, allocs.Count);
        Assert.Equal(new[] { 4, 5 }, allocs.Select(e => e.NodeId));
        Assert.Equal(20, allocs[0].CharacterLevel);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 9) npc_dialogue_started — dialog panel visible AND hover tracker at NPC entity.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Npc_dialogue_started_when_dialog_panel_visible_and_hover_points_at_npc()
    {
        var (probe, emits, state, _) = BuildProbe();
        var npc = Ent(77, "Metadata/NPC/Renly", Poe2Live.EntityCategory.Npc, grid: new Vector2(20, 20));
        state.HoverEntity = npc.Address;
        state.UiTreeElements.Add(CampaignProbe.DialogPanelSignatureSentinel);

        var uiRoots = new nint[] { 0x9000 };
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19, 19), new[] { npc }, uiRoots: uiRoots));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19, 19), new[] { npc }, uiRoots: uiRoots));  // no re-fire

        var d = emits.OfType<NpcDialogueStartedEvent>().ToList();
        Assert.Single(d);
        Assert.Equal("Metadata/NPC/Renly", d[0].NpcMetadataPath);
        Assert.Equal(16, d[0].NpcNameHash.Length);            // Task 3 HashText16 contract
        Assert.False(string.IsNullOrEmpty(d[0].DialogueTextHash));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 10) npc_dialogue_option_selected — dialog open + selection-edge sentinel.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Npc_dialogue_option_selected_fires_on_selection_edge_while_dialog_open()
    {
        var (probe, emits, state, _) = BuildProbe();
        var npc = Ent(77, "Metadata/NPC/Renly", Poe2Live.EntityCategory.Npc, grid: new Vector2(20, 20));
        state.HoverEntity = npc.Address;
        var uiRoots = new nint[] { 0x9000 };

        // Open the dialog first.
        state.UiTreeElements.Clear();
        state.UiTreeElements.Add(CampaignProbe.DialogPanelSignatureSentinel);
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19, 19), new[] { npc }, uiRoots: uiRoots));
        Assert.Single(emits.OfType<NpcDialogueStartedEvent>());

        // Now emit an option-selection edge alongside the still-open dialog.
        state.UiTreeElements.Clear();
        state.UiTreeElements.Add(CampaignProbe.DialogPanelSignatureSentinel);
        state.UiTreeElements.Add(CampaignProbe.DialogOptionSelectedSentinel);
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19, 19), new[] { npc }, uiRoots: uiRoots));

        var opts = emits.OfType<NpcDialogueOptionSelectedEvent>().ToList();
        Assert.Single(opts);
        Assert.Equal(0, opts[0].OptionIndex);
        Assert.False(string.IsNullOrEmpty(opts[0].OptionTextHash));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 11) quest_reward_selected — reward panel + selection sentinel.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Quest_reward_selected_fires_on_selection_edge_while_reward_panel_open()
    {
        var (probe, emits, state, _) = BuildProbe();
        var uiRoots = new nint[] { 0x9000 };
        state.UiTreeElements.Add(CampaignProbe.QuestRewardPanelSentinel);
        state.UiTreeElements.Add(CampaignProbe.QuestRewardSelectedSentinel);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>(), uiRoots: uiRoots));

        var rewards = emits.OfType<QuestRewardSelectedEvent>().ToList();
        Assert.Single(rewards);
        Assert.Equal(0, rewards[0].OfferIndex);
        Assert.Equal(1, rewards[0].TotalOffers);
        Assert.False(rewards[0].WasSkipped);
        Assert.False(string.IsNullOrEmpty(rewards[0].RewardDisplayNameHash));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 12) waypoint_travel — waypoint transition targeted + zone change → both AreaTransitionUsed
    //     AND WaypointTravel fire on the destination-tick.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Waypoint_travel_fires_alongside_area_transition_used_when_waypoint_transition_took_us_out()
    {
        var (probe, emits, state, _) = BuildProbe();
        var wpTransition = Ent(600, "Metadata/Transition/WaypointGate", Poe2Live.EntityCategory.Transition,
            grid: new Vector2(10, 10));
        state.Targeted[wpTransition.Address] = 1;

        probe.Tick(Snap("G1_Town", "Ogham Village", new Vector2(10, 10), new[] { wpTransition }));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50, 50), Array.Empty<Poe2Live.EntityDot>()));

        var travel = emits.OfType<WaypointTravelEvent>().ToList();
        Assert.Single(travel);
        Assert.Equal("G1_Town", travel[0].SourceArea);
        Assert.Equal("G1_2", travel[0].DestinationArea);
        Assert.Equal(0, travel[0].WaypointMenuRowIndex);
        Assert.Single(emits.OfType<AreaTransitionUsedEvent>());  // fired together
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Envelope construction — sanity that install_uuid + boot_id + schema_version + probe_capability
    // land verbatim on the wire.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Envelope_carries_install_uuid_boot_id_schema_and_probe_capability()
    {
        var (probe, emits, _, _) = BuildProbe();
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        var env = emits[0].Envelope;
        Assert.Equal("11111111-1111-4111-8111-111111111111", env.InstallUuid);
        Assert.Equal("boot-abc", env.BootId);
        Assert.Equal(1, env.SchemaVersion);
        Assert.Equal("live", env.ProbeCapability);
        Assert.Equal("act1", env.ActHint);   // G1_1 → act1
    }
}
