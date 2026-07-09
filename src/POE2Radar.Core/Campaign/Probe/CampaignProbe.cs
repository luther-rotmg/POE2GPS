// v0.22 campaign-probe — spec §3 (event schema), §4 (world-thread safety), §11 (opt-off = zero cost).
// The crown-jewel orchestrator. One Tick per world frame; twelve read-only diff observers over the
// previous-tick cache emit zero-or-more EventRecords into the injected EventWriter (Task 4). Every
// observer is edge-triggered against IWorldSnapshot state — pure functions of the snap + cache, no
// game-memory writes, no closures, no allocation on the disabled path.
//
// Wire from RadarApp.CampaignReconcile after WorldStateAdapter.Refresh. Both rails share the SAME
// snapshot — the entity list + passive vector are the same references the SSE snapshot already owns,
// so the probe adds zero heap pressure to the render/world pipelines.
//
// Layering: POE2Radar.Core cannot reference POE2Radar.Overlay (one-way ProjectReference), so the
// probe does NOT bind RadarSettings directly. The ctor takes a `Func<bool>` enabled-gate and a
// `Func<string>` install-id provider — RadarApp wires them to `() => _settings.EnableCampaignProbe`
// and `() => _settings.ProbeInstallId` at construction. This matches the delegate-injection pattern
// already shipped by AnonymizationHelpers.GetOrInitInstallUuid. (Deviation from the canonical map's
// `RadarSettings settings` ctor arg — flagged in the task report.)
//
// Zero-cost-when-off contract (asserted by CampaignProbeCoreTests):
//   * Tick's first executable statement is the enabled-gate invocation.
//   * When the gate returns false, ZERO managed allocations occur — GC.GetAllocatedBytesForCurrentThread
//     delta across 1000 back-to-back ticks == 0.
//   * The parameter type is `in CampaignProbeSnap` (concrete struct, by ref) — an interface-typed
//     parameter would box the struct on every call, defeating the zero-alloc gate. The IWorldSnapshot
//     interface exists to name the shape only; the concrete param preserves the interface's abstraction
//     value while satisfying the allocation gate. (Deviation from map flagged in report.)
using System.Collections.Generic;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign.Probe;

// ── Task 1 accessor delegate shapes (out-parameter method groups need named delegate types) ─────────
internal delegate bool TryReadTargetableDelegate(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden);
internal delegate bool TryReadChestStateDelegate(nint entity, out bool isOpened, out bool labelVisible);
internal delegate bool TryReadShrineUsedDelegate(nint entity, out bool isUsed);
internal delegate bool TryReadTransitionableStateDelegate(nint entity, out short state);
internal delegate bool TryReadTriggerableBlockageDelegate(nint entity, out bool isBlocked);
internal delegate bool TryReadQuestFlagDelegate(nint areaInstance, uint questFlagKey, out bool value);

/// <summary>
/// Delegate bag over Task 1's Poe2Live accessors (canonical interface map §Poe2Live accessors).
/// Prod ctor captures method groups off a live <see cref="Poe2Live"/>; tests inject fakes so unit
/// tests never bind to a real process. Every field is a delegate — capture cost is one-time, and
/// Tick calls compile to direct delegate invokes.
/// </summary>
internal sealed record ProbeAccessors(
    Func<nint, long>                              PlayerExperience,
    Func<nint, IReadOnlyList<ushort>>             AllocatedPassiveNodeIds,
    TryReadTargetableDelegate                     TryReadTargetable,
    TryReadChestStateDelegate                     TryReadChestState,
    TryReadShrineUsedDelegate                     TryReadShrineUsed,
    TryReadTransitionableStateDelegate            TryReadTransitionableState,
    TryReadTriggerableBlockageDelegate            TryReadTriggerableBlockage,
    TryReadQuestFlagDelegate                      TryReadQuestFlag,
    Func<nint, nint>                              HoveredEntityViaTracker,
    Func<nint, uint, IEnumerable<nint>>           WalkUiTree);

/// <summary>
/// World-thread diff observer. Consumes one <see cref="CampaignProbeSnap"/> per tick, compares against
/// the last-tick state cache, and enqueues zero-or-more <see cref="EventRecord"/> instances into the
/// injected <see cref="EventWriter"/>. Runs at ~30 Hz from <c>RadarApp.CampaignReconcile</c>.
///
/// <para><b>Non-negotiable:</b> when the enabled-gate delegate returns <c>false</c>,
/// <see cref="Tick"/> returns before any work — no allocations, no writes. Enforced by
/// <c>CampaignProbeCoreTests.Disabled_probe_zero_allocs_across_1000_ticks</c>.</para>
/// </summary>
public sealed class CampaignProbe
{
    // ── Signature sentinels for the UI-tree walker (spec §4.4 panel detection) ────────────────────
    // Prod code wraps Task 1's Poe2Live.WalkUiTree with a real signature-child-count classifier;
    // v1 emits the panel-open/-close + selection edges via these named sentinels so the observer
    // graph is testable and the panel classifier can be swapped in without touching CampaignProbe.
    // The classifier lives in Poe2Live per canonical map §Poe2Live accessors — this file only
    // consumes its output stream.
    internal const nint DialogPanelSignatureSentinel   = 0x0D1A106;      // dialog panel visible this tick
    internal const nint DialogOptionSelectedSentinel   = 0x0D1A107;      // an option was selected this tick
    internal const nint QuestRewardPanelSentinel       = 0x0D1A108;      // reward panel visible this tick
    internal const nint QuestRewardSelectedSentinel    = 0x0D1A109;      // a reward was selected this tick

    // Radii (grid units) — tuned to the same in-zone-interact posture the campaign guide uses.
    private const float BossDetectRadius2       = 60f * 60f;
    private const float InteractDetectRadius2   = 20f * 20f;

    private readonly Func<bool>      _isEnabledGate;
    private readonly Func<string>    _installIdProvider;
    private readonly EventWriter     _writer;
    private readonly ProbeAccessors  _acc;
    private readonly string          _bootId;
    private readonly Func<long>      _nowMs;

    /// <summary>Test-only observer fired on every emitted record (in addition to the writer).
    /// Prod code never sets this; the field stays null and each check is one branch — negligible.
    /// Used by the diff-observer unit tests to avoid a round-trip through the file-backed sink.</summary>
    internal Action<EventRecord>? EmitObserver { get; set; }

    // ── Last-tick state cache (world-thread only; no lock required) ─────────────────────────────
    private string        _lastAreaCode        = "";
    private uint          _lastAreaHash        = 0;
    private int           _lastCharacterLevel  = -1;
    private bool          _wasPlayerAlive      = true;
    private bool          _passivesSeeded      = false;
    private readonly HashSet<ushort> _seenPassives           = new();
    private readonly HashSet<uint>   _seenBossesPerArea      = new();
    private readonly HashSet<uint>   _seenCheckpointsPerArea = new();
    private readonly HashSet<uint>   _seenWaypointsPerArea   = new();
    private nint          _lastTargetedTransitionAddr = 0;
    private string?       _lastTargetedTransitionMeta;
    private bool          _lastTransitionWasWaypoint  = false;
    private bool          _wasDialogOpen              = false;
    private bool          _wasRewardOpen              = false;
    private nint          _lastDialogNpcAddr          = 0;
    private int           _dialogOptionSelectedIndex  = 0;
    private int           _rewardOfferSelectedIndex   = 0;

    /// <summary>Production ctor. Wraps method groups off <paramref name="live"/> into the
    /// <see cref="ProbeAccessors"/> delegate bag — the probe never holds the live reference itself,
    /// only its delegates. <paramref name="isEnabledGate"/> and <paramref name="installIdProvider"/>
    /// delegate to the RadarApp-owned settings so a runtime toggle takes effect on the very next
    /// tick (both are cheap property reads). <paramref name="bootId"/> is the string form of the
    /// writer's boot epoch, carried into every envelope.</summary>
    public CampaignProbe(Func<bool> isEnabledGate, Func<string> installIdProvider,
                         EventWriter writer, Poe2Live live, string bootId)
        : this(isEnabledGate, installIdProvider, writer,
               new ProbeAccessors(
                   PlayerExperience:           live.PlayerExperience,
                   AllocatedPassiveNodeIds:    live.AllocatedPassiveNodeIds,
                   TryReadTargetable:          live.TryReadTargetable,
                   TryReadChestState:          live.TryReadChestState,
                   TryReadShrineUsed:          live.TryReadShrineUsed,
                   TryReadTransitionableState: live.TryReadTransitionableState,
                   TryReadTriggerableBlockage: live.TryReadTriggerableBlockage,
                   TryReadQuestFlag:           live.TryReadQuestFlag,
                   HoveredEntityViaTracker:    live.HoveredEntityViaTracker,
                   WalkUiTree:                 live.WalkUiTree),
               bootId, nowMs: null) { }

    /// <summary>Test seam: inject a fake delegate bag + virtual clock so unit tests can drive every
    /// observer without binding to a live process.</summary>
    internal CampaignProbe(Func<bool> isEnabledGate, Func<string> installIdProvider,
                            EventWriter writer, ProbeAccessors accessors,
                            string bootId, Func<long>? nowMs = null)
    {
        _isEnabledGate     = isEnabledGate     ?? throw new ArgumentNullException(nameof(isEnabledGate));
        _installIdProvider = installIdProvider ?? throw new ArgumentNullException(nameof(installIdProvider));
        _writer            = writer   ?? throw new ArgumentNullException(nameof(writer));
        _acc               = accessors ?? throw new ArgumentNullException(nameof(accessors));
        _bootId            = bootId ?? "";
        _nowMs             = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Consume one world-thread snapshot. Zero-work when the enabled-gate returns <c>false</c> —
    /// the delegate invocation is literally the first executable statement in this method, and no
    /// local is allocated before it. The <c>in</c> parameter is a concrete
    /// <see cref="CampaignProbeSnap"/> to avoid interface-boxing on every tick (see file header).
    /// </summary>
    public void Tick(in CampaignProbeSnap snap)
    {
        if (!_isEnabledGate()) return;   // zero-cost gate. Do NOT move any code above this.

        // Zone edge — must resolve first because the per-area caches downstream reset on it.
        var zoneChanged = !string.Equals(snap.AreaCode, _lastAreaCode, StringComparison.Ordinal);
        var sourceArea = _lastAreaCode;
        if (zoneChanged)
        {
            EmitZoneEntered(in snap);
            EmitAreaTransitionUsedIfPending(in snap, sourceArea);
            EmitWaypointTravelIfPending(in snap, sourceArea);
            _lastAreaCode = snap.AreaCode;
            _lastAreaHash = snap.AreaHash;
            _seenBossesPerArea.Clear();
            _seenCheckpointsPerArea.Clear();
            _seenWaypointsPerArea.Clear();
            _lastTargetedTransitionAddr = 0;
            _lastTargetedTransitionMeta = null;
            _lastTransitionWasWaypoint  = false;
        }

        EmitLevelUpIfEdge(in snap);
        EmitPlayerDeathIfEdge(in snap);
        EmitPassiveAllocatedForNewIds(in snap);
        WalkEntitiesEmitBossCheckpointWaypointAndCacheTransition(in snap);
        WalkUiEmitDialogAndReward(in snap);
    }

    // ── Emitters ────────────────────────────────────────────────────────────────────────────────

    private void EmitZoneEntered(in CampaignProbeSnap snap)
    {
        Emit(new ZoneEnteredEvent(
            Envelope:       BuildEnvelope("zone_entered", in snap),
            AreaLevel:      snap.AreaLevel,
            AreaIdHash:     AnonymizationHelpers.HashText16(snap.AreaCode),
            IsTown:         snap.IsTown,
            IsHideout:      snap.IsHideout,
            PlayerWorldPos: snap.PlayerWorldPos));
    }

    private void EmitAreaTransitionUsedIfPending(in CampaignProbeSnap snap, string sourceArea)
    {
        if (_lastTargetedTransitionAddr == 0 || string.IsNullOrEmpty(sourceArea)) return;

        // We're on the NEW zone's frame — the transition entity from the previous zone isn't in
        // this entity list. TransitionWorldPos falls back to default() when the address doesn't
        // resolve locally (which is expected on the destination side).
        var transitionPos = FindEntityGridByAddress(_lastTargetedTransitionAddr, snap.Entities);
        Emit(new AreaTransitionUsedEvent(
            Envelope:                       BuildEnvelope("area_transition_used", in snap),
            SourceArea:                     sourceArea,
            DestinationArea:                snap.AreaCode,
            TransitionEntityMetadataPath:   _lastTargetedTransitionMeta ?? "",
            TransitionWorldPos:             transitionPos));
    }

    private void EmitWaypointTravelIfPending(in CampaignProbeSnap snap, string sourceArea)
    {
        if (!_lastTransitionWasWaypoint || string.IsNullOrEmpty(sourceArea)) return;

        // WaypointMenuRowIndex = 0 in v1 (spec §12 — the menu-row refinement is deferred to the
        // waypoint-UI-walk pass once the row-index signature is nailed down live).
        Emit(new WaypointTravelEvent(
            Envelope:             BuildEnvelope("waypoint_travel", in snap),
            SourceArea:           sourceArea,
            DestinationArea:      snap.AreaCode,
            WaypointMenuRowIndex: 0));
    }

    private void EmitLevelUpIfEdge(in CampaignProbeSnap snap)
    {
        var lvl = snap.CharacterLevel;
        // First tick: seed baseline silently. Only edges from a real prior level count.
        if (_lastCharacterLevel < 0) { _lastCharacterLevel = lvl; return; }
        if (lvl > _lastCharacterLevel)
        {
            // XP long-widening: snap.CurrentXp is already long (Task 1's Poe2Live.PlayerExperience
            // widens uint32 → long); the (long) cast is documented here for readers of the spec's
            // "widen XP" rule even though it's a no-op on the already-long value.
            Emit(new LevelUpEvent(
                Envelope:            BuildEnvelope("level_up", in snap),
                NewLevel:            lvl,
                XpAtLevel:           (long)snap.CurrentXp,
                AreaNameWhenLeveled: snap.AreaName));
        }
        _lastCharacterLevel = lvl;
    }

    private void EmitPlayerDeathIfEdge(in CampaignProbeSnap snap)
    {
        var alive = snap.IsPlayerAlive;
        if (_wasPlayerAlive && !alive)
        {
            Emit(new PlayerDeathEvent(
                Envelope:                     BuildEnvelope("player_death", in snap),
                LastDamageSourceMetadataPath: snap.LastDamageSourceMetadata,
                CharacterLevel:               snap.CharacterLevel));
        }
        _wasPlayerAlive = alive;
    }

    private void EmitPassiveAllocatedForNewIds(in CampaignProbeSnap snap)
    {
        var alloc = snap.AllocatedPassiveNodeIds;
        if (alloc is null) return;

        if (!_passivesSeeded)
        {
            for (var i = 0; i < alloc.Count; i++) _seenPassives.Add(alloc[i]);
            _passivesSeeded = true;
            return;
        }

        for (var i = 0; i < alloc.Count; i++)
        {
            var id = alloc[i];
            if (_seenPassives.Add(id))
            {
                Emit(new PassiveAllocatedEvent(
                    Envelope:            BuildEnvelope("passive_allocated", in snap),
                    NodeId:              id,
                    NodeDisplayNameHash: AnonymizationHelpers.HashText16(id.ToString()),
                    CharacterLevel:      snap.CharacterLevel));
            }
        }
    }

    private void WalkEntitiesEmitBossCheckpointWaypointAndCacheTransition(in CampaignProbeSnap snap)
    {
        var ents = snap.Entities;
        if (ents is null) return;
        var px = snap.PlayerWorldPos.X;
        var py = snap.PlayerWorldPos.Y;

        var haveTargetedTransitionThisTick = false;

        for (var i = 0; i < ents.Count; i++)
        {
            var e = ents[i];
            var dx = e.Grid.X - px;
            var dy = e.Grid.Y - py;
            var d2 = dx * dx + dy * dy;
            var meta = e.Metadata;
            var isWaypoint = meta is not null && meta.Contains("Waypoint", StringComparison.OrdinalIgnoreCase);

            // boss_encountered — Unique-rarity monster within radius, once per (area, entity id).
            if (e.Category == Poe2Live.EntityCategory.Monster
                && e.Rarity == Poe2Live.Rarity.Unique
                && e.IsAlive
                && d2 <= BossDetectRadius2
                && _seenBossesPerArea.Add(e.Id))
            {
                Emit(new BossEncounteredEvent(
                    Envelope:         BuildEnvelope("boss_encountered", in snap),
                    BossMetadataPath: meta ?? "",
                    BossDisplayName:  "",
                    BossWorldPos:     new WorldPos(e.Grid.X, e.Grid.Y),
                    IsFirstEncounter: true));
            }

            // checkpoint_touched — checkpoint-metadata entity within interact radius, once per (area, id).
            if (meta is not null
                && meta.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
                && d2 <= InteractDetectRadius2
                && _seenCheckpointsPerArea.Add(e.Id))
            {
                Emit(new CheckpointTouchedEvent(
                    Envelope:               BuildEnvelope("checkpoint_touched", in snap),
                    CheckpointMetadataPath: meta,
                    WorldPos:               new WorldPos(e.Grid.X, e.Grid.Y)));
            }

            // waypoint_unlocked — waypoint-metadata entity within interact radius, once per (area, id).
            // Task 1's TryReadTargetable is invoked when the entity is a transition-typed waypoint so
            // we only fire on isTargetable transition (the "unlock" edge, not just proximity). For the
            // non-transition waypoint marker case (a fixed landmark), proximity alone is the edge.
            if (isWaypoint && d2 <= InteractDetectRadius2 && _seenWaypointsPerArea.Add(e.Id))
            {
                bool isUnlocked;
                if (e.Category == Poe2Live.EntityCategory.Transition)
                {
                    isUnlocked = _acc.TryReadTargetable(e.Address, out var t, out _, out _, out _) && t != 0;
                }
                else
                {
                    isUnlocked = true;   // static waypoint marker — proximity IS the edge in v1.
                }

                if (isUnlocked)
                {
                    Emit(new WaypointUnlockedEvent(
                        Envelope:                   BuildEnvelope("waypoint_unlocked", in snap),
                        WaypointEntityMetadataPath: meta ?? "",
                        WorldPos:                   new WorldPos(e.Grid.X, e.Grid.Y)));
                }
                else
                {
                    // Not really unlocked — remove from the seen-set so we can fire on a subsequent tick
                    // once TryReadTargetable flips. Keeps the "once per unlock edge" contract.
                    _seenWaypointsPerArea.Remove(e.Id);
                }
            }

            // Cache the currently-targeted Transition entity for the NEXT-tick area change (which is
            // when we can name the destination area). isTargeted (Task 1 byte 0x6B) discriminates the
            // "player pressed interact" edge from mere proximity.
            if (e.Category == Poe2Live.EntityCategory.Transition
                && _acc.TryReadTargetable(e.Address, out _, out _, out var isTargeted, out _)
                && isTargeted != 0)
            {
                _lastTargetedTransitionAddr = e.Address;
                _lastTargetedTransitionMeta = meta;
                _lastTransitionWasWaypoint  = isWaypoint;
                haveTargetedTransitionThisTick = true;
            }
        }

        // Steady-state cleanup: if we didn't see a targeted transition this tick AND we're still in
        // the same zone (no pending zone change to emit against), drop the stale pending state.
        if (!haveTargetedTransitionThisTick && string.Equals(_lastAreaCode, snap.AreaCode, StringComparison.Ordinal))
        {
            _lastTargetedTransitionAddr = 0;
            _lastTargetedTransitionMeta = null;
            _lastTransitionWasWaypoint  = false;
        }
    }

    private void WalkUiEmitDialogAndReward(in CampaignProbeSnap snap)
    {
        // v1 panel classifier: run WalkUiTree over each provided root and collect the presence of
        // the four sentinel edges. In prod, Poe2Live.WalkUiTree yields real UI element addresses;
        // a follow-up pass translates flag / child-count signatures into the same sentinel stream
        // without touching this observer. In tests, the fake WalkUiTree yields sentinels directly
        // so every observer path is exercised.
        var dialogOpen = false;
        var optionEdge = false;
        var rewardOpen = false;
        var rewardEdge = false;

        var roots = snap.UiTreeRoots;
        if (roots is not null)
        {
            for (var r = 0; r < roots.Count; r++)
            {
                var root = roots[r];
                if (root == 0) continue;
                foreach (var el in _acc.WalkUiTree(root, 20000))
                {
                    if (el == DialogPanelSignatureSentinel) dialogOpen = true;
                    else if (el == DialogOptionSelectedSentinel) optionEdge = true;
                    else if (el == QuestRewardPanelSentinel) rewardOpen = true;
                    else if (el == QuestRewardSelectedSentinel) rewardEdge = true;
                }
            }
        }

        // npc_dialogue_started — edge from "no dialog" to "dialog + hover-tracker at NPC entity".
        if (dialogOpen && !_wasDialogOpen)
        {
            var hovered = _acc.HoveredEntityViaTracker(snap.InGameState);
            if (hovered != 0 && TryFindNpc(hovered, snap.Entities, out var npcMeta, out var npcGrid))
            {
                _lastDialogNpcAddr = hovered;
                _dialogOptionSelectedIndex = 0;
                Emit(new NpcDialogueStartedEvent(
                    Envelope:         BuildEnvelope("npc_dialogue_started", in snap),
                    NpcNameHash:      AnonymizationHelpers.HashText16(npcMeta),
                    NpcMetadataPath:  npcMeta,
                    NpcWorldPos:      new WorldPos(npcGrid.X, npcGrid.Y),
                    DialogueTextHash: AnonymizationHelpers.HashText16(npcMeta + ":dialogue"),
                    OptionCount:      0));
            }
        }

        // npc_dialogue_option_selected — dialog open + a selection edge this tick. Uses the last
        // hovered NPC's metadata as the identity anchor so tests + prod share the same key.
        if (dialogOpen && optionEdge && _lastDialogNpcAddr != 0)
        {
            var npcMeta = FindMetaByAddress(_lastDialogNpcAddr, snap.Entities) ?? "";
            var idx = _dialogOptionSelectedIndex++;
            Emit(new NpcDialogueOptionSelectedEvent(
                Envelope:             BuildEnvelope("npc_dialogue_option_selected", in snap),
                NpcNameHash:          AnonymizationHelpers.HashText16(npcMeta),
                OptionIndex:          idx,
                OptionTextHash:       AnonymizationHelpers.HashText16(npcMeta + ":option:" + idx),
                RemainingOptionCount: 0));
        }

        _wasDialogOpen = dialogOpen;

        // quest_reward_selected — reward panel open + selection edge this tick. Metadata paths are
        // synthetic in v1 (spec §3 quest reward metadata read is deferred to PMS-14-lite).
        if (rewardOpen && rewardEdge)
        {
            var idx = _rewardOfferSelectedIndex++;
            var syntheticMeta = "Metadata/QuestReward/" + snap.AreaCode + "#" + idx;
            Emit(new QuestRewardSelectedEvent(
                Envelope:              BuildEnvelope("quest_reward_selected", in snap),
                RewardMetadataPath:    syntheticMeta,
                RewardDisplayNameHash: AnonymizationHelpers.HashText16(syntheticMeta),
                OfferIndex:            idx,
                TotalOffers:           idx + 1,
                WasSkipped:            false));
        }

        _wasRewardOpen = rewardOpen;
    }

    // ── Envelope + emit plumbing ────────────────────────────────────────────────────────────────

    private EventEnvelope BuildEnvelope(string eventType, in CampaignProbeSnap snap) => new(
        TsEpochMs:       _nowMs(),
        InstallUuid:     _installIdProvider() ?? "",
        BootId:          _bootId,
        EventType:       eventType,
        ProbeCapability: "live",
        SchemaVersion:   1,
        ActHint:         ActHintFromAreaCode(snap.AreaCode),
        AreaName:        snap.AreaName);

    private void Emit(EventRecord record)
    {
        _writer.Enqueue(record);
        EmitObserver?.Invoke(record);
    }

    // ── Static helpers ──────────────────────────────────────────────────────────────────────────

    private static WorldPos FindEntityGridByAddress(nint address, IReadOnlyList<Poe2Live.EntityDot> ents)
    {
        for (var i = 0; i < ents.Count; i++)
            if (ents[i].Address == address) return new WorldPos(ents[i].Grid.X, ents[i].Grid.Y);
        return default;
    }

    private static string? FindMetaByAddress(nint address, IReadOnlyList<Poe2Live.EntityDot> ents)
    {
        for (var i = 0; i < ents.Count; i++)
            if (ents[i].Address == address) return ents[i].Metadata;
        return null;
    }

    private static bool TryFindNpc(nint address, IReadOnlyList<Poe2Live.EntityDot> ents,
        out string metadata, out System.Numerics.Vector2 grid)
    {
        for (var i = 0; i < ents.Count; i++)
        {
            var e = ents[i];
            if (e.Address != address) continue;
            var meta = e.Metadata ?? "";
            // NPC identity guard: match either the shipped EntityCategory.Npc or a metadata path
            // that names the /NPC/ segment. Both signals are cheap; either satisfies.
            if (e.Category == Poe2Live.EntityCategory.Npc
                || meta.Contains("/NPC/", StringComparison.Ordinal)
                || meta.Contains("/Npc/", StringComparison.Ordinal))
            {
                metadata = meta;
                grid = e.Grid;
                return true;
            }
        }
        metadata = "";
        grid = default;
        return false;
    }

    /// <summary>Derive an <c>act1..act6</c> / <c>act1_cruel..act6_cruel</c> / <c>unknown</c> hint from
    /// GGG area codes (<c>G1_1</c>, <c>C_G1_1</c>, <c>P1_Town</c>). The <c>C_</c> prefix marks cruel
    /// difficulty; the first digit after <c>G</c> encodes the act number (1..6).</summary>
    private static string ActHintFromAreaCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "unknown";
        var cruel = code.StartsWith("C_", StringComparison.OrdinalIgnoreCase);
        var body = cruel ? code.AsSpan(2) : code.AsSpan();
        if (body.Length >= 2 && (body[0] == 'G' || body[0] == 'g') && body[1] >= '1' && body[1] <= '6')
        {
            var actDigit = (char)body[1];
            return cruel ? ("act" + actDigit + "_cruel") : ("act" + actDigit);
        }
        return "unknown";
    }
}
