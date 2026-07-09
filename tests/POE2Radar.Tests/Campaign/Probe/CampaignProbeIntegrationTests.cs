// v0.22 campaign-probe — PROBE-TESTS §9 integration.
// End-to-end drive: construct a real EventWriter pointed at a temp dir + a real CampaignProbe
// wired through the internal test-seam ctor, drive a synthetic Act 1 sequence, then read the
// JSONL file back off disk and deserialize each line into the concrete sealed record via the
// discriminated-union serializer. Anchors the six live-at-ship "PMS-14 verification pass"
// event types so a regression in any prior task (RECORD / ANON / WRITER / CORE) surfaces here
// against the on-disk contract, not just the in-memory EmitObserver.
namespace POE2Radar.Tests.Campaign.Probe;

using POE2Radar.Core.Campaign.Probe;
using POE2Radar.Core.Game;
using POE2Radar.Tests.Campaign.Probe.Fakes;
using CampaignProbe = POE2Radar.Core.Campaign.Probe.CampaignProbe;
using Vector2 = System.Numerics.Vector2;

public sealed class CampaignProbeIntegrationTests : IDisposable
{
    private readonly string _baseDir;
    private readonly List<EventWriter> _writers = new();

    public CampaignProbeIntegrationTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "poe2gps-probe-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        foreach (var w in _writers)
        {
            try { w.DisposeAsync().AsTask().Wait(1000); } catch { /* best-effort */ }
        }
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Test seam wiring — a minimal ProbeAccessors bag over a mutable state box. Matches the
    //    shape CampaignProbeCoreTests already uses so the two suites stay conceptually aligned.
    private sealed class FakeState
    {
        public Dictionary<nint, byte> Targeted = new();
        public List<nint> UiElements = new();

        public ProbeAccessors Accessors() => new(
            PlayerExperience:           _ => 0L,
            AllocatedPassiveNodeIds:    _ => System.Array.Empty<ushort>(),
            TryReadTargetable:          (nint e, out byte t, out byte h, out byte g, out byte hd) =>
            {
                t = 0; h = 0; g = Targeted.TryGetValue(e, out var v) ? v : (byte)0; hd = 0;
                return true;
            },
            TryReadChestState:          (nint _, out bool o, out bool l) => { o = l = false; return false; },
            TryReadShrineUsed:          (nint _, out bool u) => { u = false; return false; },
            TryReadTransitionableState: (nint _, out short s) => { s = 0; return false; },
            TryReadTriggerableBlockage: (nint _, out bool b) => { b = false; return false; },
            TryReadQuestFlag:           (nint _, uint _, out bool v) => { v = false; return false; },
            HoveredEntityViaTracker:    _ => 0,
            WalkUiTree:                 (_, _) => UiElements);
    }

    private (CampaignProbe probe, EventWriter writer, FakeState state) Boot(
        string installUuid  = "11111111-1111-4111-8111-111111111111",
        long   bootEpochMs  = 1_720_000_000_000L,
        bool   enabled      = true)
    {
        var writer = new EventWriter(installUuid, bootEpochMs, _baseDir,
            // Short timer so a partial-batch trailer flushes quickly for the test.
            flushInterval: TimeSpan.FromMilliseconds(50));
        _writers.Add(writer);
        var state = new FakeState();
        long t = 1_720_000_000_100L;
        var probe = new CampaignProbe(
            isEnabledGate:     () => enabled,
            installIdProvider: () => installUuid,
            writer:            writer,
            accessors:         state.Accessors(),
            bootId:            writer.CurrentBootId,
            nowMs:             () => t++);
        return (probe, writer, state);
    }

    private static Poe2Live.EntityDot Ent(uint id, string metadata, Poe2Live.EntityCategory cat,
        Vector2 grid = default, Poe2Live.Rarity rarity = Poe2Live.Rarity.NonMonster)
        => new(id, (nint)id, grid, default, cat, metadata, 100, 100,
               Poi: false, Reaction: 0, Rarity: rarity, Opened: false);

    // Synthetic Act 1 sequence — mutates the fake between ticks so every diff observer fires
    // exactly the number of times we claim below.
    private static void RunAct1Sequence(CampaignProbe probe, EventWriter writer, FakeState state,
        FakeWorldSnapshot snap, Poe2Live.EntityDot boss, Poe2Live.EntityDot transition)
    {
        // 1) Seed baseline in the starting zone (no events fire on the seed tick beyond zone_entered).
        snap.AreaCode = "G1_1";
        snap.AreaName = "The Riverbank";
        snap.CharacterLevel = 4;
        snap.CurrentXp = 500L;
        snap.AllocatedPassiveNodeIds = new List<ushort> { 10, 11 };
        snap.PlayerWorldPos = new WorldPos(10f, 10f);
        probe.Tick(snap.ToSnap());                            // → zone_entered

        // 2) Boss walks into range in the same zone → boss_encountered fires.
        snap.Entities = new[] { boss };
        probe.Tick(snap.ToSnap());                            // → boss_encountered

        // 3) Passive tree grows by one node → passive_allocated fires for node 12.
        snap.AllocatedPassiveNodeIds = new List<ushort> { 10, 11, 12 };
        probe.Tick(snap.ToSnap());                            // → passive_allocated

        // 4) Character levels up in the same zone → level_up fires with widened XP.
        snap.CharacterLevel = 5;
        snap.CurrentXp = 1200L;
        probe.Tick(snap.ToSnap());                            // → level_up

        // 5) Targeted transition entity appears — cached for the next-tick zone change.
        snap.Entities = new[] { boss, transition };
        state.Targeted[transition.Address] = 1;
        probe.Tick(snap.ToSnap());                            // (transition cached, no emit yet)

        // 6) Zone change flushes area_transition_used against the previous zone. New zone also
        //    emits its own zone_entered — we assert on the transition edge and count zone_entered
        //    at 2 total (initial + destination).
        state.Targeted.Clear();
        snap.AreaCode = "G1_2";
        snap.AreaName = "Clearfell";
        snap.AreaHash = 0xDEADBEEFu;
        snap.Entities = System.Array.Empty<Poe2Live.EntityDot>();
        snap.PlayerWorldPos = new WorldPos(50f, 50f);
        probe.Tick(snap.ToSnap());                            // → area_transition_used + zone_entered

        // 7) Player dies in the destination zone → player_death fires with the last damage source.
        snap.LastDamageSourceMetadata = "Metadata/Monsters/AmbushAssassin";
        snap.IsPlayerAlive = false;
        probe.Tick(snap.ToSnap());                            // → player_death

        writer.FlushSync();
    }

    [Fact]
    public async Task Act1_sequence_emits_six_named_event_types_to_jsonl_on_disk()
    {
        var (probe, writer, state) = Boot();
        var snap = new FakeWorldSnapshot();
        var boss = Ent(500, "Metadata/Monsters/Boss/HillockAct1", Poe2Live.EntityCategory.Monster,
            grid: new Vector2(12, 12), rarity: Poe2Live.Rarity.Unique);
        var transition = Ent(600, "Metadata/Terrain/Act1/Transition_G1_1_to_G1_2",
            Poe2Live.EntityCategory.Transition, grid: new Vector2(15, 15));

        RunAct1Sequence(probe, writer, state, snap, boss, transition);
        await writer.FlushAsync();

        var lines = await ReadJsonLinesAsync(writer.CurrentFilePath);

        // Sequence produces 7 lines: 2 zone_entered (initial + destination) + boss + passive
        // + level_up + area_transition_used + player_death = 6 distinct event types.
        Assert.Equal(7, lines.Length);

        // Every line round-trips through the discriminated-union deserializer.
        var records = lines.Select(EventRecordJson.Deserialize<EventRecord>).ToList();

        // Six named event types called out by PMS-14 verification pass.
        var types = records.Select(r => r.Envelope.EventType).ToList();
        Assert.Equal(2, types.Count(t => t == "zone_entered"));
        Assert.Single(types, t => t == "boss_encountered");
        Assert.Single(types, t => t == "passive_allocated");
        Assert.Single(types, t => t == "level_up");
        Assert.Single(types, t => t == "area_transition_used");
        Assert.Single(types, t => t == "player_death");

        // Envelope is populated on every line — probe_capability "live" is the ship-time contract.
        foreach (var r in records)
        {
            Assert.Equal("live",                                   r.Envelope.ProbeCapability);
            Assert.Equal(1,                                        r.Envelope.SchemaVersion);
            Assert.Equal("11111111-1111-4111-8111-111111111111",   r.Envelope.InstallUuid);
            Assert.Equal(writer.CurrentBootId,                     r.Envelope.BootId);
            Assert.False(string.IsNullOrEmpty(r.Envelope.EventType));
            Assert.False(string.IsNullOrEmpty(r.Envelope.ActHint));
            Assert.False(string.IsNullOrEmpty(r.Envelope.AreaName));
        }

        // Anchor concrete fields on the destination-side records.
        var transitionRec = records.OfType<AreaTransitionUsedEvent>().Single();
        Assert.Equal("G1_1", transitionRec.SourceArea);
        Assert.Equal("G1_2", transitionRec.DestinationArea);
        Assert.Equal("Metadata/Terrain/Act1/Transition_G1_1_to_G1_2",
            transitionRec.TransitionEntityMetadataPath);

        var bossRec = records.OfType<BossEncounteredEvent>().Single();
        Assert.Equal("Metadata/Monsters/Boss/HillockAct1", bossRec.BossMetadataPath);
        Assert.True(bossRec.IsFirstEncounter);

        var passiveRec = records.OfType<PassiveAllocatedEvent>().Single();
        Assert.Equal(12, passiveRec.NodeId);

        var lvlRec = records.OfType<LevelUpEvent>().Single();
        Assert.Equal(5, lvlRec.NewLevel);
        Assert.Equal(1200L, lvlRec.XpAtLevel);
        Assert.Equal("The Riverbank", lvlRec.AreaNameWhenLeveled);

        var deathRec = records.OfType<PlayerDeathEvent>().Single();
        Assert.Equal("Metadata/Monsters/AmbushAssassin", deathRec.LastDamageSourceMetadataPath);
        Assert.Equal(5, deathRec.CharacterLevel);
    }

    // ── XP long-widening (finding #17) ─────────────────────────────────────────────────────────
    // Task 2's serializer must round-trip a > 2^32 XP value without truncating to 32 bits. Anchors
    // the shipped LevelUpEvent.XpAtLevel long path all the way through the JSONL file on disk.

    [Fact]
    public async Task LevelUp_xp_above_2_pow_32_serializes_full_digits_and_deserializes_intact()
    {
        var (probe, writer, _) = Boot(
            installUuid: "22222222-2222-4222-8222-222222222222",
            bootEpochMs: 1_720_000_000_001L);
        var snap = new FakeWorldSnapshot { CharacterLevel = 99, CurrentXp = 4_000_000_000L };
        probe.Tick(snap.ToSnap());                    // seed baseline

        // 5_000_000_000 > 2^32 (4_294_967_296). Truncating to uint32 would flip the low 32 bits
        // into 705_032_704, so a survival past this line proves the long path holds end-to-end.
        snap.CharacterLevel = 100;
        snap.CurrentXp = 5_000_000_000L;
        probe.Tick(snap.ToSnap());                    // → level_up with the big XP
        await writer.FlushAsync();

        var lines = await ReadJsonLinesAsync(writer.CurrentFilePath);
        var lvlLine = Assert.Single(lines, l =>
            l.Contains("\"event_type\":\"level_up\"", StringComparison.Ordinal));

        // Byte-for-byte: full decimal digits present in the emitted JSONL string.
        Assert.Contains("\"xp_at_level\":5000000000", lvlLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"xp_at_level\":705032704", lvlLine, StringComparison.Ordinal);

        // And through the round-trip: the deserialized long carries the same value.
        var rec = EventRecordJson.Deserialize<LevelUpEvent>(lvlLine);
        Assert.Equal(5_000_000_000L, rec.XpAtLevel);
        Assert.Equal(100, rec.NewLevel);
    }

    // ── One-line static assertion that FakeWorldSnapshot honours every IWorldSnapshot member ─
    // A compile-time sanity that the fake covers the shipped surface — if IWorldSnapshot grows,
    // this line fails to build and pulls the fake up alongside.

    [Fact]
    public void FakeWorldSnapshot_implements_IWorldSnapshot_surface()
    {
        IWorldSnapshot snap = new FakeWorldSnapshot();
        // Touching every declared property proves the fake fulfils the contract.
        _ = snap.AreaCode;
        _ = snap.AreaName;
        _ = snap.AreaHash;
        _ = snap.AreaLevel;
        _ = snap.IsTown;
        _ = snap.IsHideout;
        _ = snap.PlayerWorldPos;
        _ = snap.CharacterLevel;
        _ = snap.CurrentXp;
        _ = snap.IsPlayerAlive;
        _ = snap.UiTreeRoots;
        _ = snap.Entities;
        _ = snap.AllocatedPassiveNodeIds;
        _ = snap.LastDamageSourceMetadata;
        _ = snap.AreaInstance;
        _ = snap.InGameState;
        _ = snap.LocalPlayer;
        Assert.NotNull(snap);
    }

    // ── File reader that plays nice with the writer's open handle ──────────────────────────────
    // Same sharing contract EventWriterTests uses — the writer holds the file open with
    // FileShare.Read; the reader opens with FileShare.ReadWrite so Windows doesn't reject the
    // second handle mid-run.
    private static async Task<string[]> ReadJsonLinesAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) lines.Add(line);
        return lines.ToArray();
    }
}
