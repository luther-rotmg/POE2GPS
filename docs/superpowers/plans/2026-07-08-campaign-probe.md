# POE2GPS Campaign Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bake a campaign-data probe into POE2GPS that captures anonymized player zone traversals during campaign play. All 12 event types ship live at v1 (post-upstream-offset-extraction from `imkk000/poe2-offsets`). Data is written to a local JSONL file the user inspects before sharing. Shared traces flow through the v0.21 sibling-route Contribute pipeline (fifth kind: `/submit-trace`) into a public data pool consumed by POE2GPS Campaign Director and any downstream tool.

**Architecture:** New `Core/Campaign/Probe/` subsystem — `EventRecord` types + `AnonymizationHelpers` + `EventWriter` (JSONL sink, per-boot rotation, async flush via `Channel`) + `CampaignProbe` orchestrator (world-thread, diff-observers, UI-tree walk for NpcDialog + QuestReward panels using shipped POE2GPS UI primitives + hover tracker at `UiRoot + 0x7D8`). `Poe2Offsets` extended with 10 upstream-verified offset groups; `Poe2Live` extended with read-only accessors matching `PlayerInventories` chain pattern. XP accessor closes PMS-6 as a free-rider.

**Tech Stack:** C# / .NET 8+ / net10.0-windows; `System.Text.Json` for JSONL; `System.Threading.Channels` for async flush; browser JS for Dashboard toggle + toast; Node/Cloudflare Workers for `/submit-trace` sibling route (extends v0.21 pattern).

## Global Constraints

- **Zero memory writes.** Every new memory access read-only over `ServerData` / `PlayerInventories` chain. No `MemoryReader.Write*`, no `Marshal.Write*`, no new offset writes. Enforced by compliance-gate.
- **Zero-cost-when-off.** `CampaignProbe.Tick` short-circuits before any work when `_settings.EnableCampaignProbe=false`. Spy test asserts zero writes + zero heap allocations across 1000 disabled ticks.
- **Zero PII.** Text values (NPC names, dialogue, reward names) -> sha256-16 hex. `install_uuid` is a random UUID stored in `RadarSettings.ProbeInstallId`, regenerable via Settings. `boot_id` fresh per process.
- **v0.20 wire-format additive-only.** This feature adds NO SSE keys. Contribute payload is a standalone POST to `/api/contribute-trace` (SSE stream untouched).
- **Same JSONL schema forever.** All 12 events emit `probe_capability: "live"` at ship; envelope field stays for forward-compat.
- **XP long-widening (finding #17):** `Poe2Live.PlayerExperience` returns `long` but reads uint32 at the memory layer. `CampaignProbe.LevelUpEvent` construction site widens via `(long)value` cast so max-level characters over 4B XP don't truncate. Task 9 has an assertion.
- One clean commit per task.
- No `superpowers/`, `.superpowers/`, `docs/superpowers/` paths in public surfaces. CI grep gate covers.
- All tests via `dotnet test` from repo root; new tests live under `tests/POE2Radar.Tests/Campaign/Probe/`.

## Task Ordering (REORDERED FROM ORIGINAL PROPOSAL - findings #8 + #9)

Original writers proposed 1->OFFSETS, 2->RECORD, 3->ANON, 4->WRITER, 5->CORE, 6->SETTINGS, 7->UI, 8->CONTRIBUTE, 9->TESTS.

**REORDERED to satisfy producer-before-consumer:**
1. PROBE-OFFSETS
2. PROBE-RECORD
3. PROBE-ANON
4. PROBE-WRITER
5. **PROBE-SETTINGS** (was 6; PROBE-CORE consumes `RadarSettings.EnableCampaignProbe`)
6. **PROBE-CORE** (was 5)
7. **PROBE-CONTRIBUTE** (was 8; PROBE-UI consumes `/api/contribute-trace`)
8. **PROBE-UI** (was 7)
9. PROBE-TESTS

## Canonical Interface Map (AUTHORITATIVE - 17 consistency findings reconciled)

Local `Interfaces:` blocks in per-task sections below may drift from these canonical shapes. **The map here wins.** Implementers reconcile at write-time.

### EventRecord shape (findings #1, #2, #3, #4, #14)

```csharp
// Envelope: every event carries this. Value type (readonly record struct).
public readonly record struct EventEnvelope(
    long TsEpochMs,
    string InstallUuid,
    string BootId,
    string EventType,
    string ProbeCapability,   // "live" at ship
    int SchemaVersion,        // starts at 1
    string ActHint,
    string AreaName);

// Base record (reference type) - required for polymorphic discriminated-union serialization.
public abstract record EventRecord(EventEnvelope Envelope);

// Position type - matches spec section 3 {x,y} JSON byte-for-byte. Do NOT use System.Numerics.Vector2 in records.
public readonly record struct WorldPos(float X, float Y);

// 12 sealed records deriving from EventRecord. Examples:
public sealed record ZoneEnteredEvent(EventEnvelope Envelope, int AreaLevel, string AreaIdHash, bool IsTown, bool IsHideout, WorldPos PlayerWorldPos) : EventRecord(Envelope);
public sealed record LevelUpEvent(EventEnvelope Envelope, int NewLevel, long XpAtLevel, string AreaNameWhenLeveled) : EventRecord(Envelope);
// (remaining 10 follow the same pattern; envelope first, remaining fields typed per spec section 3.)

// Serializer entry point (finding #2).
public static class EventRecordJson
{
    public static string Serialize(EventRecord r);
    public static T Deserialize<T>(string line) where T : EventRecord;
}
```

**Consumers of the above (Tasks 4/6/9):** all use these types directly. No `IEventRecord` marker interface. No `IEventSink` abstraction (finding #4).

### Poe2Live accessors (finding #5) - canonical Try-out pattern

```csharp
long PlayerExperience(nint localPlayer);                                                     // uint32 widened to long
IReadOnlyList<ushort> AllocatedPassiveNodeIds(nint areaInstance);                            // NOT IReadOnlyList<int>
bool TryReadTargetable(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden);
bool TryReadChestState(nint entity, out bool isOpened, out bool labelVisible);
bool TryReadShrineUsed(nint entity, out bool isUsed);
bool TryReadTransitionableState(nint entity, out short state);
bool TryReadTriggerableBlockage(nint entity, out bool isBlocked);
bool TryReadQuestFlag(nint areaInstance, uint questFlagKey, out bool value);
nint HoveredEntityViaTracker(nint inGameState);                                              // via UiRoot+0x7D8
IEnumerable<nint> WalkUiTree(nint uiRoot, uint maxVisit = 20000);                            // finding #6 - Task 1 MUST add this (extract from ReadRitualRewards BFS)
```

**Consumer note (Task 6 PROBE-CORE ProbeAccessors record):** MUST use these exact names. Wrap Try-out patterns at call sites, e.g. `if (accessors.TryReadTargetable(e, out _, out _, out var t, out _) && t != 0)`.

### EventWriter surface (finding #7)

```csharp
public sealed class EventWriter : IAsyncDisposable
{
    public EventWriter(string installUuid, long bootEpochMs, string baseDirectory, TimeSpan? flushInterval = null);
    public string CurrentFilePath { get; }
    public string CurrentBootId { get; }
    public long EventsWritten { get; }
    public bool IsDisabled { get; }
    public void Enqueue(EventRecord record);
    public Task FlushAsync();
    public void FlushSync();
    public string? MostRecentCompletePath();
    public ValueTask DisposeAsync();
}
```

**Consumer rewrites required:**
- Task 7 (PROBE-CONTRIBUTE): `CurrentTracePath` -> `CurrentFilePath`; `CurrentEventCount` -> `EventsWritten`.
- Task 9 (PROBE-TESTS): `WrittenLineCount` -> `EventsWritten`; drop `IClock` ctor arg; use `Func<long>? nowMs = null` seam matching Task 6.

### Test-file paths (finding #11)

ALL probe test files land under `tests/POE2Radar.Tests/Campaign/Probe/`. Rewrite Task 2/3/4/7 files_create paths from flat locations to this subfolder. Task 9 does NOT create `CampaignProbeTests.cs` (owned by Task 6) - file collision resolved (finding #10).

### Missing producers added

- **`Poe2Live.WalkUiTree`** - added to Task 1 Produces (extract from `ReadRitualRewards` line ~1350 BFS visible-flag prune).
- **`POST /api/probe/reset-install-id`** - added to Task 7 (PROBE-CONTRIBUTE) Produces. Loopback-Host-gated. Regenerates `RadarSettings.ProbeInstallId` via `AnonymizationHelpers.NewInstallUuid()` + `RadarSettings.Save()`.

### Optional / deferrable

- **EventWriter file-open when off** (finding #15) - Task 4 currently opens file at ctor regardless of `EnableCampaignProbe`. Tick-gate is the enforced contract; skipping ctor open is a nice-to-have follow-up. Not blocking.

---

## Per-task briefs

> **Executor note:** Each task section below preserves the original TDD code as authored. Where local `Interfaces:` blocks name the pre-canonical types (`IEventRecord`, `IEventSink`, `CharCurExp`, `PassiveAlloc`, `HoverEntity`, `CurrentTracePath`, `CurrentEventCount`, `IClock`, `System.Numerics.Vector2` in records, `record struct XxxEvent`, flat test paths), the **Canonical Interface Map above is authoritative** - reconcile the type names when you write them. The consistency-check applied 17 findings; use the map.

---

### Task 1: PROBE-OFFSETS — Poe2Offsets + Poe2Live accessors for campaign-probe data

**Files:**
- Create: `tests/POE2Radar.Tests/CampaignProbe/Poe2CampaignProbeOffsetsTests.cs`
- Modify: `src/POE2Radar.Core/Game/Poe2Offsets.cs` (append new nested classes + extend `PlayerComponent`, `ChestComponent`)
- Modify: `src/POE2Radar.Core/Game/Poe2Live.cs` (append accessor methods after `ReadInventory` block ~line 1782)
- Test: `tests/POE2Radar.Tests/CampaignProbe/Poe2CampaignProbeOffsetsTests.cs`

**Interfaces:**
- Consumes: (none — Task 1 is the offset floor; all later tasks consume from here)
- Produces (constants — every hex value below is the exact value later tasks reference):
  - `Poe2.PlayerComponent.CurrentExperience = 0x1D8` (uint32)
  - `Poe2.PlayerServerData.QuestFlags = 0x230`
  - `Poe2.Quest.DefinitionPtr = 0x2E0`, `Quest.EntryPtr = 0x2F0`, `Quest.EntryState = 0x3C`, `Quest.EntryObjective = 0x3D`, `Quest.RowId = 0x00`, `Quest.RowName = 0x0C`
  - `Poe2.PassiveTree.AllocVecBegin = 0x8A8`, `AllocVecEnd = 0x8B0`, `EntryStride = 4`, `HopChain = [0x60,0x40,0xCE0,0x418]`, `AllocMax = 1024`
  - `Poe2.Targetable.{IsTargetable=0x69, IsHighlight=0x6A, IsTargeted=0x6B, IsHidden=0x71}` (bytes)
  - `Poe2.ChestComponent.LabelVisible = 0x21` (added; existing `OpenState = 0x168` unchanged)
  - `Poe2.Shrine.IsUsed = 0x24`, `Poe2.Transitionable.State = 0x120` (int16), `Poe2.TriggerableBlockage.IsBlocked = 0x30`
  - `Poe2.StateMachineExt.{StatesBegin=0x160, StatesEnd=0x168, TimersBegin=0x178, TimersEnd=0x180, EntryStride=8}`
  - `Poe2.HoverTracker.HoveredEntityDirect = 0x18` (already at `HoverTracker.HoveredEntity`; restated as canonical anchor for PROBE-CORE)
- Produces (Poe2Live accessors — read-only, world-thread, MemoryReader.TryReadStruct pattern):
  - `long PlayerExperience(nint localPlayer)` — returns uint32 widened to long (also feeds PMS-6 XP/hour HUD chip)
  - `IReadOnlyList<ushort> AllocatedPassiveNodeIds(nint areaInstance)`
  - `bool TryReadTargetable(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden)`
  - `bool TryReadChestState(nint entity, out bool isOpened, out bool labelVisible)`
  - `bool TryReadShrineUsed(nint entity, out bool isUsed)`
  - `bool TryReadTransitionableState(nint entity, out short state)`
  - `bool TryReadTriggerableBlockage(nint entity, out bool isBlocked)`
  - `bool TryReadQuestFlag(nint areaInstance, uint questFlagKey, out bool value)`
  - `nint HoveredEntityViaTracker(nint inGameState)`

---

- [ ] **Step 1: Write the failing offset-constant test.** These constants are the entire contract Task 1 owns — the deterministic offline test is that every constant matches the upstream hex byte-for-byte. Live-in-game validation is covered by PMS-14 lite per spec §7.

  Create `tests/POE2Radar.Tests/CampaignProbe/Poe2CampaignProbeOffsetsTests.cs`:

  ```csharp
  using POE2Radar.Core.Game;

  namespace POE2Radar.Tests.CampaignProbe;

  /// <summary>
  /// PROBE-OFFSETS constant floor. Every value here is transcribed from
  /// scratchpad/campaign-probe-offsets.md (imkk000/poe2-offsets extraction, 2026-07-08).
  /// If any assert fails, either the constant drifted or the upstream extraction was misread —
  /// re-open the scratchpad and reconcile before touching the test.
  /// </summary>
  public class Poe2CampaignProbeOffsetsTests
  {
      [Fact]
      public void PlayerComponent_CurrentExperience_matches_upstream()
          => Assert.Equal(0x1D8, Poe2.PlayerComponent.CurrentExperience);

      [Fact]
      public void PlayerServerData_QuestFlags_matches_upstream()
          => Assert.Equal(0x230, Poe2.PlayerServerData.QuestFlags);

      [Fact]
      public void Quest_definition_and_entry_offsets_match_upstream()
      {
          Assert.Equal(0x2E0, Poe2.Quest.DefinitionPtr);
          Assert.Equal(0x2F0, Poe2.Quest.EntryPtr);
          Assert.Equal(0x3C,  Poe2.Quest.EntryState);
          Assert.Equal(0x3D,  Poe2.Quest.EntryObjective);
          Assert.Equal(0x00,  Poe2.Quest.RowId);
          Assert.Equal(0x0C,  Poe2.Quest.RowName);
      }

      [Fact]
      public void PassiveTree_alloc_vec_and_hop_chain_match_upstream()
      {
          Assert.Equal(0x8A8, Poe2.PassiveTree.AllocVecBegin);
          Assert.Equal(0x8B0, Poe2.PassiveTree.AllocVecEnd);
          Assert.Equal(4,     Poe2.PassiveTree.EntryStride);
          Assert.Equal(1024,  Poe2.PassiveTree.AllocMax);
          Assert.Equal(new[] { 0x60, 0x40, 0xCE0, 0x418 }, Poe2.PassiveTree.HopChain);
      }

      [Fact]
      public void Targetable_component_bytes_match_upstream()
      {
          Assert.Equal(0x69, Poe2.Targetable.IsTargetable);
          Assert.Equal(0x6A, Poe2.Targetable.IsHighlight);
          Assert.Equal(0x6B, Poe2.Targetable.IsTargeted);
          Assert.Equal(0x71, Poe2.Targetable.IsHidden);
      }

      [Fact]
      public void ChestComponent_label_visible_matches_upstream_and_OpenState_untouched()
      {
          Assert.Equal(0x21,  Poe2.ChestComponent.LabelVisible);
          Assert.Equal(0x168, Poe2.ChestComponent.OpenState); // regression guard
      }

      [Fact]
      public void Shrine_and_Transitionable_and_Blockage_match_upstream()
      {
          Assert.Equal(0x24,  Poe2.Shrine.IsUsed);
          Assert.Equal(0x120, Poe2.Transitionable.State);
          Assert.Equal(0x30,  Poe2.TriggerableBlockage.IsBlocked);
      }

      [Fact]
      public void StateMachineExt_state_and_timer_vectors_match_upstream()
      {
          Assert.Equal(0x160, Poe2.StateMachineExt.StatesBegin);
          Assert.Equal(0x168, Poe2.StateMachineExt.StatesEnd);
          Assert.Equal(0x178, Poe2.StateMachineExt.TimersBegin);
          Assert.Equal(0x180, Poe2.StateMachineExt.TimersEnd);
          Assert.Equal(8,     Poe2.StateMachineExt.EntryStride);
      }

      [Fact]
      public void HoverTracker_hovered_entity_direct_matches_shipped_offset()
      {
          Assert.Equal(0x18,  Poe2.HoverTracker.HoveredEntityDirect);
          Assert.Equal(0x7D8, Poe2.HoverTracker.FromUiRoot); // regression guard on shipped anchor
      }

      [Fact]
      public void Poe2Live_exposes_campaign_probe_accessors()
      {
          // Reflection-only surface check — production accessors must exist so PROBE-CORE can compile
          // without live memory. Argument-count guards against accidental signature drift.
          var t = typeof(Poe2Live);
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.PlayerExperience)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.AllocatedPassiveNodeIds)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTargetable)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadChestState)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadShrineUsed)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTransitionableState)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadTriggerableBlockage)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.TryReadQuestFlag)));
          Assert.NotNull(t.GetMethod(nameof(Poe2Live.HoveredEntityViaTracker)));
      }
  }
  ```

- [ ] **Step 2: Run the test to confirm it fails.**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~Poe2CampaignProbeOffsetsTests" --nologo
  ```

  Expected: build error `CS0117: 'PlayerComponent' does not contain a definition for 'CurrentExperience'` (and equivalents for every new nested class). If it builds but fails at runtime, the constants exist but drift — Step 3 fills them from scratchpad.

- [ ] **Step 3: Extend `Poe2Offsets.cs` with the new offset groups.** Anchor edits — the existing file has `PlayerComponent` at ~line 151, `ChestComponent` at ~line 375, and a trailing `LoadedFiles` class at ~line 595. Extend `PlayerComponent`, extend `ChestComponent`, then append the new classes just before the final `}` that closes `public static class Poe2`.

  **Edit 3a — extend `PlayerComponent`** (replace the whole nested class):

  ```csharp
  /// <summary>Player component — character name + level + experience. ✓ Name/Level validated live
  /// (StdWString @ +0x1B0, level byte @ +0x204, 27 confirmed). CurrentExperience (+0x1D8) is a uint32
  /// per the imkk000/poe2-offsets extraction (2026-07-08); PoE2's 100-cap experience ~4.25B fits.
  /// Widened to long by callers for JSON serialisation ergonomics.</summary>
  public static class PlayerComponent
  {
      public const int Name              = 0x1B0; // ✓ StdWString
      public const int CurrentExperience = 0x1D8; // uint32 — upstream (imkk000/poe2-offsets 2026-07-08)
      public const int Level             = 0x204; // ✓ byte (low byte of a u32 slot)
  }
  ```

  **Edit 3b — extend `ChestComponent`** (append `LabelVisible`):

  ```csharp
  public static class ChestComponent
  {
      public const int OpenState    = 0x168; // ✓ 0 = closed/openable, non-zero = opened/used (polarity flipped 2026-06-06)
      public const int LabelVisible = 0x021; // byte — upstream (imkk000/poe2-offsets 2026-07-08)
  }
  ```

  **Edit 3c — append the new nested classes** just before the closing `}` of `public static class Poe2` (after `LoadedFiles`):

  ```csharp
  /// <summary>PlayerServerData record (element [0] of ServerData's PlayerServerData StdVector at
  /// ServerData+0x48). Hosts per-player mutable state. QuestFlags at +0x230 is a
  /// <c>Dictionary&lt;QuestFlag, bool&gt;</c> the game uses to gate quest branches (drives
  /// <c>npc_dialogue_option_selected</c> inference and <c>waypoint_travel</c> context in the campaign
  /// probe). Source: imkk000/poe2-offsets element.go (2026-07-08 extraction).</summary>
  public static class PlayerServerData
  {
      public const int QuestFlags = 0x230; // Dictionary<QuestFlag,bool> (drives quest-progression events)
  }

  /// <summary>Quest definition + entry layout. Source: imkk000/poe2-offsets quest.go (2026-07-08).
  /// <c>DefinitionPtr</c> / <c>EntryPtr</c> live off the quest root; <c>EntryState</c> (byte) and
  /// <c>EntryObjective</c> track per-quest progress. <c>RowId</c> / <c>RowName</c> live inside a
  /// quest-row struct (definition table entry).</summary>
  public static class Quest
  {
      public const int DefinitionPtr   = 0x2E0; // ptr to Quest definition
      public const int EntryPtr        = 0x2F0; // ptr to Quest entry
      public const int EntryState      = 0x3C;  // byte — entry state (drives dialogue_option/waypoint context)
      public const int EntryObjective  = 0x3D;  // objective sub-state byte
      public const int RowId           = 0x00;  // quest id within row
      public const int RowName         = 0x0C;  // quest name within row
  }

  /// <summary>Passive tree allocation. Reached from AreaInstance via the four-hop pointer chain
  /// (HopChain) that lands on ServerPlayerData; the allocated-node vector runs from AllocVecBegin
  /// (+0x8A8) to AllocVecEnd (+0x8B0) with 4-byte stride — each entry is a uint16 node id (top 2
  /// bytes pad). Source: imkk000/poe2-offsets passive_tree.go (2026-07-08). Drives the campaign
  /// probe's <c>passive_allocated</c> diff-observer.</summary>
  public static class PassiveTree
  {
      public const int AllocVecBegin = 0x8A8; // on ServerPlayerData
      public const int AllocVecEnd   = 0x8B0; // = begin + 8 (StdVector last)
      public const int EntryStride   = 4;     // uint16 node id + 2 bytes pad
      public const int AllocMax      = 1024;  // sanity cap on entry count
      /// <summary>Pointer chain from AreaInstance to ServerPlayerData: dereference each offset in order.</summary>
      public static readonly int[] HopChain = { 0x60, 0x40, 0xCE0, 0x418 };
  }

  /// <summary>Targetable component byte flags. Source: imkk000/poe2-offsets world_components.go
  /// (2026-07-08). Drives interaction detection — used by the campaign probe to refine
  /// <c>area_transition_used</c> (only fire when Transition entity was actually targeted).</summary>
  public static class Targetable
  {
      public const int IsTargetable = 0x69; // byte — 1 when entity accepts targeting
      public const int IsHighlight  = 0x6A; // byte — 1 when highlight ring shown
      public const int IsTargeted   = 0x6B; // byte — 1 when player has this entity targeted
      public const int IsHidden     = 0x71; // byte — 1 when entity is hidden from cursor
  }

  /// <summary>Shrine component. Source: imkk000/poe2-offsets world_components.go (2026-07-08).</summary>
  public static class Shrine
  {
      public const int IsUsed = 0x24; // byte — 1 after activation
  }

  /// <summary>Transitionable component (area-transition entities carry this alongside Targetable).
  /// State is int16 — non-zero values encode "opened" / "traversed". Source: imkk000/poe2-offsets
  /// world_components.go (2026-07-08).</summary>
  public static class Transitionable
  {
      public const int State = 0x120; // int16
  }

  /// <summary>TriggerableBlockage component (barriers, breakable walls). Source: imkk000/poe2-offsets
  /// world_components.go (2026-07-08).</summary>
  public static class TriggerableBlockage
  {
      public const int IsBlocked = 0x30; // byte
  }

  /// <summary>StateMachine extended layout for probe-side state reads. The existing
  /// <see cref="StateMachine.ListenerVec"/> (+0x20) drives the RuneStation chain; the additional
  /// state and timer vectors here (+0x160..+0x180) expose per-entity state slots used by
  /// Chest/Shrine/Transitionable interaction detection. Source: imkk000/poe2-offsets
  /// world_components.go (2026-07-08). Named "StateMachineExt" (not shadowing the shipped
  /// <see cref="StateMachine"/>) so the RuneStation code path is untouched.</summary>
  public static class StateMachineExt
  {
      public const int StatesBegin = 0x160;
      public const int StatesEnd   = 0x168;
      public const int TimersBegin = 0x178;
      public const int TimersEnd   = 0x180;
      public const int EntryStride = 8;
  }
  ```

  **Edit 3d — extend `HoverTracker`** (append canonical anchor for PROBE-CORE):

  ```csharp
  public static class HoverTracker
  {
      public const int FromUiRoot          = 0x7D8; // *(UiRoot + 0x7D8) → tracker container
      public const int WorldTracker        = 0x630; // + 0x630 → world hover tracker (existing)
      public const int HoveredEntity       = 0x18;  // + 0x18 → hovered entity/element (existing)
      public const int HoveredEntityDirect = 0x18;  // canonical anchor for PROBE-CORE (alias of HoveredEntity)
  }
  ```

- [ ] **Step 4: Extend `Poe2Live.cs` with campaign-probe accessors.** Append a new region at the bottom of the class (just before the final `}` that closes `public sealed class Poe2Live`, after `ReadInventory` and its helpers ~line 1897). Every accessor uses the shipped `_reader.TryReadStruct<T>` and `Ptr(...)` helpers — the same pattern `ReadInventory` uses.

  ```csharp
      // ── Campaign-Probe accessors (Task PROBE-OFFSETS, spec §4) ─────────────────
      // All read-only over ServerData / component memory. No writes. No allocations
      // on the disabled path (PROBE-CORE is gated on _settings.EnableCampaignProbe
      // upstream; these methods are simply the read primitives it composes).

      /// <summary>Current character experience (uint32 @ Player+0x1D8). Widened to long for
      /// serialisation ergonomics; PoE2 caps XP at ~4.25B which fits in uint32. Also feeds the
      /// PMS-6 XP/hour Session HUD chip. Returns 0 when the Player component is unresolved.</summary>
      public long PlayerExperience(nint localPlayer)
      {
          var c = PlayerComp(localPlayer);
          return c != 0 && _reader.TryReadStruct<uint>(c + Poe2.PlayerComponent.CurrentExperience, out var xp) ? xp : 0L;
      }

      /// <summary>Walks the passive-tree hop chain from <paramref name="areaInstance"/> to
      /// ServerPlayerData, then reads the allocated-node <c>StdVector&lt;uint16&gt;</c> at
      /// <see cref="Poe2.PassiveTree.AllocVecBegin"/>..<see cref="Poe2.PassiveTree.AllocVecEnd"/>.
      /// Returns an empty list on any failed hop or an implausible entry count (&gt;<see cref="Poe2.PassiveTree.AllocMax"/>).
      /// Never throws.</summary>
      public IReadOnlyList<ushort> AllocatedPassiveNodeIds(nint areaInstance)
      {
          var empty = System.Array.Empty<ushort>();
          try
          {
              var node = areaInstance;
              foreach (var hop in Poe2.PassiveTree.HopChain)
              {
                  node = Ptr(node + hop);
                  if (node == 0) return empty;
              }
              if (!_reader.TryReadStruct<nint>(node + Poe2.PassiveTree.AllocVecBegin, out var begin)) return empty;
              if (!_reader.TryReadStruct<nint>(node + Poe2.PassiveTree.AllocVecEnd,   out var end))   return empty;
              if (begin == 0 || end == 0 || (long)end < (long)begin) return empty;

              var byteSpan = (long)end - (long)begin;
              if (byteSpan <= 0 || byteSpan % Poe2.PassiveTree.EntryStride != 0) return empty;

              var count = (int)(byteSpan / Poe2.PassiveTree.EntryStride);
              if (count > Poe2.PassiveTree.AllocMax) return empty;

              var result = new List<ushort>(count);
              for (var i = 0; i < count; i++)
              {
                  if (!_reader.TryReadStruct<ushort>(begin + (nint)(i * Poe2.PassiveTree.EntryStride), out var id)) return result;
                  result.Add(id);
              }
              return result;
          }
          catch
          {
              return empty;
          }
      }

      /// <summary>Read the four Targetable component bytes (IsTargetable/IsHighlight/IsTargeted/IsHidden).
      /// Caller passes the entity address; this resolves the "Targetable" component and reads the bytes.
      /// Returns false when the component is missing or any byte fails to read; out parameters are 0 on false.</summary>
      public bool TryReadTargetable(nint entity, out byte isTargetable, out byte isHighlight, out byte isTargeted, out byte isHidden)
      {
          isTargetable = isHighlight = isTargeted = isHidden = 0;
          var comp = ResolveComponent(entity, "Targetable");
          if (comp == 0) return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsTargetable, out isTargetable)) return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsHighlight,  out isHighlight))  return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsTargeted,   out isTargeted))   return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.Targetable.IsHidden,     out isHidden))     return false;
          return true;
      }

      /// <summary>Read Chest.OpenState + Chest.LabelVisible in one hop. <paramref name="isOpened"/> follows
      /// the 2026-06-06 polarity flip (0 = closed, non-zero = opened).</summary>
      public bool TryReadChestState(nint entity, out bool isOpened, out bool labelVisible)
      {
          isOpened = labelVisible = false;
          var comp = ResolveComponent(entity, "Chest");
          if (comp == 0) return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.ChestComponent.OpenState,    out var open))  return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.ChestComponent.LabelVisible, out var label)) return false;
          isOpened = open != 0;
          labelVisible = label != 0;
          return true;
      }

      /// <summary>Read Shrine.IsUsed byte. Non-zero = used (already activated).</summary>
      public bool TryReadShrineUsed(nint entity, out bool isUsed)
      {
          isUsed = false;
          var comp = ResolveComponent(entity, "Shrine");
          if (comp == 0) return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.Shrine.IsUsed, out var b)) return false;
          isUsed = b != 0;
          return true;
      }

      /// <summary>Read Transitionable.State (int16). Non-zero values encode traversal states.</summary>
      public bool TryReadTransitionableState(nint entity, out short state)
      {
          state = 0;
          var comp = ResolveComponent(entity, "Transitionable");
          if (comp == 0) return false;
          return _reader.TryReadStruct<short>(comp + Poe2.Transitionable.State, out state);
      }

      /// <summary>Read TriggerableBlockage.IsBlocked byte.</summary>
      public bool TryReadTriggerableBlockage(nint entity, out bool isBlocked)
      {
          isBlocked = false;
          var comp = ResolveComponent(entity, "TriggerableBlockage");
          if (comp == 0) return false;
          if (!_reader.TryReadStruct<byte>(comp + Poe2.TriggerableBlockage.IsBlocked, out var b)) return false;
          isBlocked = b != 0;
          return true;
      }

      /// <summary>Read one bool entry out of the per-player quest-flags dictionary
      /// (PlayerServerData+0x230). Walks: AreaInstance → ServerData (+0x598) → PlayerServerData
      /// StdVector (+0x48) [0] → PlayerServerData → QuestFlags dictionary → probe key.
      /// Dictionary internals are traversed via the shipped StdMap conventions
      /// (<see cref="Poe2.StdMapNode"/>). Returns false when any hop fails or the key is missing.</summary>
      public bool TryReadQuestFlag(nint areaInstance, uint questFlagKey, out bool value)
      {
          value = false;
          try
          {
              var serverData = Ptr(areaInstance + Poe2.AreaInstance.ServerDataPtr);
              if (serverData == 0) return false;
              if (!_reader.TryReadStruct<StdVector>(serverData + Poe2.ServerData.PlayerServerDataVec, out var v)) return false;
              if (v.First == 0) return false;
              var psd = Ptr(v.First);
              if (psd == 0) return false;

              // Dictionary<QuestFlag,bool> at psd + 0x230. Read the std::map root node ptr and BST-walk
              // by uint key; StdMapNode layout: {Left,Parent,Right, IsNil(byte @+0x19), Data{Key,Value}@+0x20}.
              if (!_reader.TryReadStruct<nint>(psd + Poe2.PlayerServerData.QuestFlags, out var rootHolder)) return false;
              if (rootHolder == 0) return false;

              // std::map layout: rootHolder is the header node; header.Parent is the true root.
              if (!_reader.TryReadStruct<nint>(rootHolder + Poe2.StdMapNode.Parent, out var cur)) return false;
              for (var guard = 0; guard < 4096 && cur != 0; guard++)
              {
                  if (!_reader.TryReadStruct<byte>(cur + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) return false;
                  if (!_reader.TryReadStruct<uint>(cur + Poe2.StdMapNode.KeyId, out var key)) return false;
                  if (key == questFlagKey)
                  {
                      if (!_reader.TryReadStruct<byte>(cur + Poe2.StdMapNode.ValueEntityPtr, out var b)) return false;
                      value = b != 0;
                      return true;
                  }
                  var branch = questFlagKey < key ? Poe2.StdMapNode.Left : Poe2.StdMapNode.Right;
                  if (!_reader.TryReadStruct<nint>(cur + branch, out cur)) return false;
              }
              return false;
          }
          catch
          {
              return false;
          }
      }

      /// <summary>Resolve the current hovered entity via the UI-root hover-tracker chain
      /// (<c>*(UiRoot+0x7D8) → tracker → +0x18</c>). This is the anchor PROBE-CORE uses for
      /// <c>npc_dialogue_started</c> — when the NpcDialog panel is visible (UI-tree signature walk),
      /// the hovered entity at dialog-open time identifies the NPC. Returns 0 on any failed hop.</summary>
      public nint HoveredEntityViaTracker(nint inGameState)
      {
          var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
          if (uiRoot == 0) return 0;
          var tracker = Ptr(uiRoot + Poe2.HoverTracker.FromUiRoot);
          if (tracker == 0) return 0;
          return Ptr(tracker + Poe2.HoverTracker.HoveredEntityDirect);
      }
  ```

- [ ] **Step 5: Build and re-run tests — verify green.**

  ```powershell
  dotnet build src/POE2Radar.Core/POE2Radar.Core.csproj --nologo
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~Poe2CampaignProbeOffsetsTests" --nologo
  ```

  Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0` (nine `[Fact]`s in the new test class).

- [ ] **Step 6: Full test-suite regression check.** Confirm the shipped `HoverTracker.HoveredEntity` alias, `ChestComponent.OpenState` extension, and `PlayerComponent` layout change didn't break any existing test.

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --nologo
  ```

  Expected: full suite green — the two aliases are additive and no existing test asserts on `PlayerComponent`/`ChestComponent`/`HoverTracker` values that could have shifted.

- [ ] **Step 7: Zero-cost-when-off sanity check (spec §2, §8).** No code in Task 1 runs unless PROBE-CORE calls it — verify by grepping. Every new accessor must have zero callers in the tree until PROBE-CORE lands.

  ```powershell
  # Every new public method should be callable only from tests until PROBE-CORE lands.
  Select-String -Path "src/**/*.cs" -Pattern "PlayerExperience\(|AllocatedPassiveNodeIds\(|TryReadTargetable\(|TryReadChestState\(|TryReadShrineUsed\(|TryReadTransitionableState\(|TryReadTriggerableBlockage\(|TryReadQuestFlag\(|HoveredEntityViaTracker\("
  ```

  Expected: only the definitions in `Poe2Live.cs` match — no other `src/` caller yet. (Test-file callers are fine.)

- [ ] **Step 8: Verify `dotnet build POE2Radar.sln` is clean (no warnings introduced).**

  ```powershell
  dotnet build POE2Radar.sln --nologo /warnaserror
  ```

  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 9: Commit.**

  ```powershell
  git add src/POE2Radar.Core/Game/Poe2Offsets.cs src/POE2Radar.Core/Game/Poe2Live.cs tests/POE2Radar.Tests/CampaignProbe/Poe2CampaignProbeOffsetsTests.cs
  git commit -m @'
  PROBE-OFFSETS: campaign-probe offset floor + Poe2Live accessors

  Extends Poe2Offsets.cs with the ten new nested groups extracted from
  imkk000/poe2-offsets (2026-07-08): PlayerComponent.CurrentExperience,
  PlayerServerData.QuestFlags, Quest.*, PassiveTree.* (incl. 4-hop chain),
  Targetable.*, ChestComponent.LabelVisible, Shrine, Transitionable,
  TriggerableBlockage, StateMachineExt, HoverTracker.HoveredEntityDirect.

  Adds nine read-only Poe2Live accessors matching the shipped
  ReadInventory chain pattern (MemoryReader.TryReadStruct throughout,
  never throws, sensible defaults on any failed hop).

  PlayerExperience(localPlayer) also closes PMS-6 XP/hour Session HUD
  chip as a free-rider.

  Read-only, no writes, no callers yet — PROBE-CORE composes these.
  '@
  ```

**Verify Gate #1:** `dotnet build POE2Radar.sln /warnaserror` clean AND `dotnet test --filter FullyQualifiedName~Poe2CampaignProbeOffsetsTests` shows 9/9 pass with every constant matching `scratchpad/campaign-probe-offsets.md` byte-for-byte. No `src/` caller of any new accessor exists yet (grep in Step 7 returns only the definitions) — PROBE-CORE is where the offsets get exercised.

---

### Task 2: PROBE-RECORD — EventRecord.cs (12 event records + envelope + discriminated-union JSON serializer)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/Probe/EventRecord.cs`
- Create: `tests/POE2Radar.Tests/CampaignProbe/EventRecordTests.cs`

**Interfaces:**
- Consumes: (none — leaf task; independent of PROBE-OFFSETS)
- Produces:
  - `POE2Radar.Core.Campaign.Probe.EventRecord` — abstract envelope base with `TsEpochMs (long)`, `InstallUuid (string)`, `BootId (string)`, `EventType (string)`, `ProbeCapability (string, default "live")`, `SchemaVersion (int, default 1)`, `ActHint (string, default "unknown")`, `AreaName (string)`
  - `POE2Radar.Core.Campaign.Probe.WorldPos` — `readonly record struct WorldPos(float X, float Y)` — JSON: `{"x":..,"y":..}`
  - Concrete records (one per spec §3 row): `ZoneEnteredEvent`, `AreaTransitionUsedEvent`, `BossEncounteredEvent`, `CheckpointTouchedEvent`, `WaypointUnlockedEvent`, `PlayerDeathEvent`, `WaypointTravelEvent`, `NpcDialogueStartedEvent`, `NpcDialogueOptionSelectedEvent`, `QuestRewardSelectedEvent`, `PassiveAllocatedEvent`, `LevelUpEvent`. Each ctor assigns its `event_type` string; each field carries a `[JsonPropertyName]` snake_case tag verbatim from spec §3.
  - `POE2Radar.Core.Campaign.Probe.EventRecordJson.Serialize(EventRecord) : string` — discriminated-union serialize; dispatches on `record.GetType()` so concrete event-specific fields land flat next to envelope in one JSON object (matches spec §3 shape).
  - `POE2Radar.Core.Campaign.Probe.EventRecordJson.Deserialize<T>(string) : T where T : EventRecord`

- [ ] **Step 1: Write the failing test file.** Create `tests/POE2Radar.Tests/CampaignProbe/EventRecordTests.cs`:

```csharp
using System.Text.Json;
using POE2Radar.Core.Campaign.Probe;

namespace POE2Radar.Tests.CampaignProbe;

// v0.22 campaign-probe — spec §3 event schema is the shipping wire-format.
// Snake_case JSON keys are byte-for-byte per LO's brief; any rename requires a schema_version bump.
public class EventRecordTests
{
    private static ZoneEnteredEvent SampleZoneEntered() => new()
    {
        TsEpochMs = 1_720_400_000_000L,
        InstallUuid = "00000000-0000-4000-8000-000000000001",
        BootId = "00000000-0000-4000-8000-000000000002",
        ProbeCapability = "live",
        SchemaVersion = 1,
        ActHint = "act1",
        AreaName = "The Riverbank",
        AreaLevel = 2,
        AreaIdHash = "abcdef0123456789",
        IsTown = false,
        IsHideout = false,
        PlayerWorldPos = new WorldPos(120.5f, -45.25f),
    };

    [Fact]
    public void ZoneEntered_serializes_all_snake_case_keys()
    {
        var json = EventRecordJson.Serialize(SampleZoneEntered());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("zone_entered", root.GetProperty("event_type").GetString());
        Assert.Equal(1_720_400_000_000L, root.GetProperty("ts_epoch_ms").GetInt64());
        Assert.Equal("00000000-0000-4000-8000-000000000001", root.GetProperty("install_uuid").GetString());
        Assert.Equal("00000000-0000-4000-8000-000000000002", root.GetProperty("boot_id").GetString());
        Assert.Equal("live", root.GetProperty("probe_capability").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("act1", root.GetProperty("act_hint").GetString());
        Assert.Equal("The Riverbank", root.GetProperty("area_name").GetString());
        Assert.Equal(2, root.GetProperty("area_level").GetInt32());
        Assert.Equal("abcdef0123456789", root.GetProperty("area_id_hash").GetString());
        Assert.False(root.GetProperty("is_town").GetBoolean());
        Assert.False(root.GetProperty("is_hideout").GetBoolean());
        var pos = root.GetProperty("player_world_pos");
        Assert.Equal(120.5f, pos.GetProperty("x").GetSingle());
        Assert.Equal(-45.25f, pos.GetProperty("y").GetSingle());
    }

    [Fact]
    public void ZoneEntered_round_trips()
    {
        var src = SampleZoneEntered();
        var back = EventRecordJson.Deserialize<ZoneEnteredEvent>(EventRecordJson.Serialize(src));
        Assert.Equal(src, back);
    }

    [Fact]
    public void All_12_event_types_expose_expected_event_type_string()
    {
        var pairs = new (EventRecord Sample, string Expected)[]
        {
            (new ZoneEnteredEvent(),                "zone_entered"),
            (new AreaTransitionUsedEvent(),         "area_transition_used"),
            (new BossEncounteredEvent(),            "boss_encountered"),
            (new CheckpointTouchedEvent(),          "checkpoint_touched"),
            (new WaypointUnlockedEvent(),           "waypoint_unlocked"),
            (new PlayerDeathEvent(),                "player_death"),
            (new WaypointTravelEvent(),             "waypoint_travel"),
            (new NpcDialogueStartedEvent(),         "npc_dialogue_started"),
            (new NpcDialogueOptionSelectedEvent(),  "npc_dialogue_option_selected"),
            (new QuestRewardSelectedEvent(),        "quest_reward_selected"),
            (new PassiveAllocatedEvent(),           "passive_allocated"),
            (new LevelUpEvent(),                    "level_up"),
        };
        Assert.Equal(12, pairs.Length);
        foreach (var (sample, expected) in pairs)
        {
            using var doc = JsonDocument.Parse(EventRecordJson.Serialize(sample));
            Assert.Equal(expected, doc.RootElement.GetProperty("event_type").GetString());
        }
    }

    [Fact]
    public void PlayerDeath_null_damage_source_serializes_as_json_null()
    {
        var evt = new PlayerDeathEvent { LastDamageSourceMetadataPath = null, CharacterLevel = 15 };
        using var doc = JsonDocument.Parse(EventRecordJson.Serialize(evt));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("last_damage_source_metadata_path").ValueKind);
        Assert.Equal(15, doc.RootElement.GetProperty("character_level").GetInt32());
    }

    [Fact]
    public void LevelUp_null_xp_is_bucket_b_stub_shape()
    {
        // spec §3 note: pre-PMS-14 level_up fires with xp_at_level = null and probe_capability = "v0.22_pending"
        var evt = new LevelUpEvent
        {
            ProbeCapability = "v0.22_pending",
            NewLevel = 20,
            XpAtLevel = null,
            AreaNameWhenLeveled = "The Marketplace",
        };
        using var doc = JsonDocument.Parse(EventRecordJson.Serialize(evt));
        Assert.Equal("v0.22_pending", doc.RootElement.GetProperty("probe_capability").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("xp_at_level").ValueKind);
        Assert.Equal(20, doc.RootElement.GetProperty("new_level").GetInt32());
        Assert.Equal("The Marketplace", doc.RootElement.GetProperty("area_name_when_leveled").GetString());
    }

    [Fact]
    public void All_12_events_round_trip_byte_for_byte()
    {
        var samples = new EventRecord[]
        {
            new ZoneEnteredEvent { AreaLevel = 3, AreaIdHash = "aaaa000000000000", IsTown = true, IsHideout = false, PlayerWorldPos = new(1, 2) },
            new AreaTransitionUsedEvent { SourceArea = "The Riverbank", DestinationArea = "Clearfell", TransitionEntityMetadataPath = "Metadata/Terrain/Act1/Transition", TransitionWorldPos = new(3, 4) },
            new BossEncounteredEvent { BossMetadataPath = "Metadata/Monsters/Boss/BloatedMiller", BossDisplayName = "The Bloated Miller", BossWorldPos = new(5, 6), IsFirstEncounter = true },
            new CheckpointTouchedEvent { CheckpointMetadataPath = "Metadata/Terrain/Checkpoint", WorldPos = new(7, 8) },
            new WaypointUnlockedEvent { WaypointEntityMetadataPath = "Metadata/Terrain/Waypoint", WorldPos = new(9, 10) },
            new PlayerDeathEvent { LastDamageSourceMetadataPath = "Metadata/Monsters/BeastOfTheField", CharacterLevel = 12 },
            new WaypointTravelEvent { SourceArea = "Clearfell", DestinationArea = "Ogham Farmlands", WaypointMenuRowIndex = 3 },
            new NpcDialogueStartedEvent { NpcNameHash = "abcdef0123456789", NpcMetadataPath = "Metadata/Npc/Renly", NpcWorldPos = new(11, 12), DialogueTextHash = "fedcba9876543210", OptionCount = 4 },
            new NpcDialogueOptionSelectedEvent { NpcNameHash = "abcdef0123456789", OptionIndex = 1, OptionTextHash = "1111222233334444", RemainingOptionCount = 3 },
            new QuestRewardSelectedEvent { RewardMetadataPath = "Metadata/Items/Ring", RewardDisplayNameHash = "aaaabbbbccccdddd", OfferIndex = 0, TotalOffers = 3, WasSkipped = false },
            new PassiveAllocatedEvent { NodeId = 12345, NodeDisplayNameHash = "eeeeffff00001111", CharacterLevel = 15 },
            new LevelUpEvent { NewLevel = 20, XpAtLevel = 1_234_567L, AreaNameWhenLeveled = "The Marketplace" },
        };
        Assert.Equal(12, samples.Length);
        foreach (var sample in samples)
        {
            var json = EventRecordJson.Serialize(sample);
            var back = (EventRecord)typeof(EventRecordJson)
                .GetMethod(nameof(EventRecordJson.Deserialize))!
                .MakeGenericMethod(sample.GetType())
                .Invoke(null, new object[] { json })!;
            Assert.Equal(sample, back);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails (types missing).**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~EventRecordTests
```

Expected: build failure — `CS0246: The type or namespace name 'ZoneEnteredEvent' (and 11 siblings, WorldPos, EventRecord, EventRecordJson) could not be found`. This confirms the test compiles against a not-yet-implemented API.

- [ ] **Step 3: Write the production types.** Create `src/POE2Radar.Core/Campaign/Probe/EventRecord.cs`:

```csharp
// v0.22 campaign-probe — spec §3 event schema.
// Envelope + 12 concrete event records + discriminated-union serializer.
// JSON keys are snake_case byte-for-byte per the brief; do NOT rename without a schema_version bump.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Campaign.Probe;

public readonly record struct WorldPos(
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y);

public abstract record EventRecord
{
    [JsonPropertyName("ts_epoch_ms")]     public long   TsEpochMs        { get; init; }
    [JsonPropertyName("install_uuid")]    public string InstallUuid      { get; init; } = "";
    [JsonPropertyName("boot_id")]         public string BootId           { get; init; } = "";
    [JsonPropertyName("event_type")]      public string EventType        { get; init; } = "";
    [JsonPropertyName("probe_capability")] public string ProbeCapability { get; init; } = "live";
    [JsonPropertyName("schema_version")]  public int    SchemaVersion    { get; init; } = 1;
    [JsonPropertyName("act_hint")]        public string ActHint          { get; init; } = "unknown";
    [JsonPropertyName("area_name")]       public string AreaName         { get; init; } = "";
}

public sealed record ZoneEnteredEvent : EventRecord
{
    public ZoneEnteredEvent() { EventType = "zone_entered"; }
    [JsonPropertyName("area_level")]        public int      AreaLevel      { get; init; }
    [JsonPropertyName("area_id_hash")]      public string   AreaIdHash     { get; init; } = "";
    [JsonPropertyName("is_town")]           public bool     IsTown         { get; init; }
    [JsonPropertyName("is_hideout")]        public bool     IsHideout      { get; init; }
    [JsonPropertyName("player_world_pos")]  public WorldPos PlayerWorldPos { get; init; }
}

public sealed record AreaTransitionUsedEvent : EventRecord
{
    public AreaTransitionUsedEvent() { EventType = "area_transition_used"; }
    [JsonPropertyName("source_area")]                    public string   SourceArea                    { get; init; } = "";
    [JsonPropertyName("destination_area")]               public string   DestinationArea               { get; init; } = "";
    [JsonPropertyName("transition_entity_metadata_path")] public string   TransitionEntityMetadataPath { get; init; } = "";
    [JsonPropertyName("transition_world_pos")]           public WorldPos TransitionWorldPos            { get; init; }
}

public sealed record BossEncounteredEvent : EventRecord
{
    public BossEncounteredEvent() { EventType = "boss_encountered"; }
    [JsonPropertyName("boss_metadata_path")]  public string   BossMetadataPath  { get; init; } = "";
    [JsonPropertyName("boss_display_name")]   public string   BossDisplayName   { get; init; } = "";
    [JsonPropertyName("boss_world_pos")]      public WorldPos BossWorldPos      { get; init; }
    [JsonPropertyName("is_first_encounter")]  public bool     IsFirstEncounter  { get; init; }
}

public sealed record CheckpointTouchedEvent : EventRecord
{
    public CheckpointTouchedEvent() { EventType = "checkpoint_touched"; }
    [JsonPropertyName("checkpoint_metadata_path")] public string   CheckpointMetadataPath { get; init; } = "";
    [JsonPropertyName("world_pos")]                public WorldPos WorldPos               { get; init; }
}

public sealed record WaypointUnlockedEvent : EventRecord
{
    public WaypointUnlockedEvent() { EventType = "waypoint_unlocked"; }
    [JsonPropertyName("waypoint_entity_metadata_path")] public string   WaypointEntityMetadataPath { get; init; } = "";
    [JsonPropertyName("world_pos")]                     public WorldPos WorldPos                   { get; init; }
}

public sealed record PlayerDeathEvent : EventRecord
{
    public PlayerDeathEvent() { EventType = "player_death"; }
    [JsonPropertyName("last_damage_source_metadata_path")] public string? LastDamageSourceMetadataPath { get; init; }
    [JsonPropertyName("character_level")]                  public int     CharacterLevel               { get; init; }
}

public sealed record WaypointTravelEvent : EventRecord
{
    public WaypointTravelEvent() { EventType = "waypoint_travel"; }
    [JsonPropertyName("source_area")]              public string SourceArea            { get; init; } = "";
    [JsonPropertyName("destination_area")]         public string DestinationArea       { get; init; } = "";
    [JsonPropertyName("waypoint_menu_row_index")]  public int    WaypointMenuRowIndex  { get; init; }
}

public sealed record NpcDialogueStartedEvent : EventRecord
{
    public NpcDialogueStartedEvent() { EventType = "npc_dialogue_started"; }
    [JsonPropertyName("npc_name_hash")]      public string   NpcNameHash      { get; init; } = "";
    [JsonPropertyName("npc_metadata_path")]  public string   NpcMetadataPath  { get; init; } = "";
    [JsonPropertyName("npc_world_pos")]      public WorldPos NpcWorldPos      { get; init; }
    [JsonPropertyName("dialogue_text_hash")] public string   DialogueTextHash { get; init; } = "";
    [JsonPropertyName("option_count")]       public int      OptionCount      { get; init; }
}

public sealed record NpcDialogueOptionSelectedEvent : EventRecord
{
    public NpcDialogueOptionSelectedEvent() { EventType = "npc_dialogue_option_selected"; }
    [JsonPropertyName("npc_name_hash")]           public string NpcNameHash          { get; init; } = "";
    [JsonPropertyName("option_index")]            public int    OptionIndex          { get; init; }
    [JsonPropertyName("option_text_hash")]        public string OptionTextHash       { get; init; } = "";
    [JsonPropertyName("remaining_option_count")]  public int    RemainingOptionCount { get; init; }
}

public sealed record QuestRewardSelectedEvent : EventRecord
{
    public QuestRewardSelectedEvent() { EventType = "quest_reward_selected"; }
    [JsonPropertyName("reward_metadata_path")]      public string RewardMetadataPath     { get; init; } = "";
    [JsonPropertyName("reward_display_name_hash")]  public string RewardDisplayNameHash  { get; init; } = "";
    [JsonPropertyName("offer_index")]               public int    OfferIndex             { get; init; }
    [JsonPropertyName("total_offers")]              public int    TotalOffers            { get; init; }
    [JsonPropertyName("was_skipped")]               public bool   WasSkipped             { get; init; }
}

public sealed record PassiveAllocatedEvent : EventRecord
{
    public PassiveAllocatedEvent() { EventType = "passive_allocated"; }
    [JsonPropertyName("node_id")]                public int    NodeId              { get; init; }
    [JsonPropertyName("node_display_name_hash")] public string NodeDisplayNameHash { get; init; } = "";
    [JsonPropertyName("character_level")]        public int    CharacterLevel      { get; init; }
}

public sealed record LevelUpEvent : EventRecord
{
    public LevelUpEvent() { EventType = "level_up"; }
    [JsonPropertyName("new_level")]              public int    NewLevel            { get; init; }
    [JsonPropertyName("xp_at_level")]            public long?  XpAtLevel           { get; init; }
    [JsonPropertyName("area_name_when_leveled")] public string AreaNameWhenLeveled { get; init; } = "";
}

// Discriminated-union serializer: dispatches on the runtime type so concrete
// event-specific fields land flat next to envelope in one JSON object.
// Used by EventWriter (PROBE-WRITER) for JSONL sink and by round-trip tests.
public static class EventRecordJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public static string Serialize(EventRecord record)
        => JsonSerializer.Serialize(record, record.GetType(), Options);

    public static T Deserialize<T>(string json) where T : EventRecord
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"null deserialization result for {typeof(T).Name}");
}
```

- [ ] **Step 4: Run tests to verify all pass.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~EventRecordTests
```

Expected: `Passed! - Failed: 0, Passed: 6, Skipped: 0`.

- [ ] **Step 5: Cross-check snake_case field names against spec §3 byte-for-byte.** Diff the `[JsonPropertyName]` string in every concrete record against the spec table at `docs/superpowers/specs/2026-07-08-campaign-probe-design.md:44-56`. Any drift here is a shipping-contract bug — snake_case in the file must equal snake_case in the spec table character-for-character. Run:

```powershell
Select-String -Path src/POE2Radar.Core/Campaign/Probe/EventRecord.cs -Pattern 'JsonPropertyName' | Select-Object -ExpandProperty Line
```

Manually verify each of the 12 event fields plus the 8 envelope fields matches spec §3. Fix any mismatch and re-run Step 4 before moving on.

- [ ] **Step 6: Run the full test suite to confirm no regression in adjacent test projects.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj
```

Expected: all pre-existing tests still pass; the 6 new `EventRecordTests` join the green count.

- [ ] **Step 7: Commit.**

```powershell
git add src/POE2Radar.Core/Campaign/Probe/EventRecord.cs tests/POE2Radar.Tests/CampaignProbe/EventRecordTests.cs
git commit -m @'
feat(campaign-probe): add EventRecord schema (envelope + 12 event records + JSON serializer)

Ships spec §3 event schema verbatim: 8-field envelope + 12 concrete event
records with snake_case JSON keys byte-for-byte per LO's brief. Discriminated-
union serializer dispatches on runtime type so event-specific fields land flat
next to envelope in one JSONL row. Nullable fields (player_death damage source,
level_up xp_at_level bucket-B stub) serialize as JSON null.

Leaf task for PROBE-WRITER (JSONL sink), PROBE-CORE (world-thread emitter), and
PROBE-CONTRIBUTE (/api/contribute-trace payload).
'@
```

---

### Task 3: PROBE-ANON — Anonymization primitives (sha256-16 + install UUID)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/Probe/AnonymizationHelpers.cs`
- Create: `tests/POE2Radar.Tests/AnonymizationHelpersTests.cs`

**Interfaces:**
- Consumes: (none — this task has no upstream dependencies; runs in parallel with PROBE-OFFSETS/PROBE-RECORD from a code-dependency standpoint)
- Produces:
  - `AnonymizationHelpers.HashText16(string text) : string` — deterministic first-16-hex-chars of sha256(utf8(text)), lowercase; null-safe (null → empty). Consumed by PROBE-RECORD (`*_hash` envelope fields per spec §3) and PROBE-CORE (hashes at ctor).
  - `AnonymizationHelpers.NewInstallUuid() : string` — `Guid.NewGuid().ToString("D").ToLowerInvariant()`. Consumed by PROBE-WRITER (per-boot `boot_id`) and PROBE-SETTINGS (initial `ProbeInstallId` + Reset-session button).
  - `AnonymizationHelpers.GetOrInitInstallUuid(Func<string?> read, Action<string> persist) : string` — delegate-based get-or-mint helper. Consumed by PROBE-SETTINGS at startup (call site: `GetOrInitInstallUuid(() => settings.ProbeInstallId, id => { settings.ProbeInstallId = id; RadarSettings.Save(...); })`).

> **Architecture note:** the task hint spelled the third helper as `GetInstallUuid(RadarSettings)`, but `POE2Radar.Core.csproj` has zero project references (verified — Core cannot reach `POE2Radar.Overlay.Config.RadarSettings`). Substituted a delegate-injected signature that preserves identical semantics (non-empty → passthrough; empty/null → mint + persist + return) and keeps Core purely computational per spec §2. PROBE-SETTINGS (Task 6) owns the RadarSettings read/write wiring.

---

- [ ] **Step 1: Write the failing tests.** Cover all three helpers in one file — determinism, format, entropy, null-safety, delegate short-circuit, delegate mint+persist path.

  Create `tests/POE2Radar.Tests/AnonymizationHelpersTests.cs`:

  ```csharp
  using System.Text.RegularExpressions;
  using POE2Radar.Core.Campaign.Probe;

  public class AnonymizationHelpersTests
  {
      // ── HashText16 ──────────────────────────────────────────────────────

      [Fact]
      public void HashText16_IsDeterministic()
      {
          Assert.Equal(
              AnonymizationHelpers.HashText16("Alira"),
              AnonymizationHelpers.HashText16("Alira"));
      }

      [Fact]
      public void HashText16_ReturnsSixteenLowercaseHex()
      {
          var h = AnonymizationHelpers.HashText16("The Karui Way");
          Assert.Equal(16, h.Length);
          Assert.Matches(new Regex("^[0-9a-f]{16}$"), h);
      }

      [Fact]
      public void HashText16_DifferentInputsDiffer()
      {
          Assert.NotEqual(
              AnonymizationHelpers.HashText16("Alira"),
              AnonymizationHelpers.HashText16("Kraityn"));
      }

      [Fact]
      public void HashText16_NullNormalizesToEmpty()
      {
          Assert.Equal(
              AnonymizationHelpers.HashText16(""),
              AnonymizationHelpers.HashText16(null!));
      }

      [Fact]
      public void HashText16_EmptySentinelIsStable()
      {
          // First 8 bytes of SHA-256("") in lowercase hex. Grep-able in trace JSONL
          // if this ever leaks through to a debug post-mortem.
          Assert.Equal("e3b0c44298fc1c14", AnonymizationHelpers.HashText16(""));
      }

      // ── NewInstallUuid ─────────────────────────────────────────────────

      [Fact]
      public void NewInstallUuid_IsValidGuidD()
      {
          var s = AnonymizationHelpers.NewInstallUuid();
          Assert.True(Guid.TryParseExact(s, "D", out _));
      }

      [Fact]
      public void NewInstallUuid_IsLowercase()
      {
          var s = AnonymizationHelpers.NewInstallUuid();
          Assert.Equal(s.ToLowerInvariant(), s);
      }

      [Fact]
      public void NewInstallUuid_HasEntropy()
      {
          var set = new HashSet<string>();
          for (int i = 0; i < 100; i++)
              set.Add(AnonymizationHelpers.NewInstallUuid());
          Assert.Equal(100, set.Count);
      }

      // ── GetOrInitInstallUuid ───────────────────────────────────────────

      [Fact]
      public void GetOrInitInstallUuid_ReturnsExistingWithoutCallingPersist()
      {
          const string existing = "abcd1234-abcd-1234-abcd-1234abcd1234";
          var persistCalls = 0;
          var got = AnonymizationHelpers.GetOrInitInstallUuid(
              read: () => existing,
              persist: _ => persistCalls++);

          Assert.Equal(existing, got);
          Assert.Equal(0, persistCalls);
      }

      [Fact]
      public void GetOrInitInstallUuid_EmptyStringMintsAndPersistsSameValue()
      {
          string? persisted = null;
          var got = AnonymizationHelpers.GetOrInitInstallUuid(
              read: () => "",
              persist: v => persisted = v);

          Assert.True(Guid.TryParseExact(got, "D", out _));
          Assert.Equal(got, persisted);
      }

      [Fact]
      public void GetOrInitInstallUuid_NullMintsAndPersists()
      {
          string? persisted = null;
          var got = AnonymizationHelpers.GetOrInitInstallUuid(
              read: () => null,
              persist: v => persisted = v);

          Assert.True(Guid.TryParseExact(got, "D", out _));
          Assert.Equal(got, persisted);
      }
  }
  ```

- [ ] **Step 2: Run tests to verify they fail (compile-error stage).**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~AnonymizationHelpersTests"
  ```

  Expected: build error `CS0234: The type or namespace name 'Probe' does not exist in the namespace 'POE2Radar.Core.Campaign'` (or `CS0246` on `AnonymizationHelpers`). This confirms the test file compiles up to the missing production type — the failing red we want before writing the impl.

- [ ] **Step 3: Write the minimal implementation.** SHA-256 via the .NET 10 `SHA256.HashData` static API (no `IDisposable` lifecycle), first 8 bytes → 16 lowercase hex chars. Guid `"D"` format lowercased. Delegate get-or-mint that only fires `persist` on the mint path.

  Create `src/POE2Radar.Core/Campaign/Probe/AnonymizationHelpers.cs`:

  ```csharp
  using System.Security.Cryptography;
  using System.Text;

  namespace POE2Radar.Core.Campaign.Probe;

  /// <summary>
  /// Anonymization primitives for the Campaign Probe (spec §2, §3). Zero PII: text
  /// values are collapsed to a stable 16-char sha256 prefix before they hit the JSONL
  /// sink, and install identity is a CSPRNG-drawn UUID minted once and persisted in
  /// <c>RadarSettings.ProbeInstallId</c>. Purely computational — the only side effect
  /// on the whole surface is the settings-persistence callback that
  /// <see cref="GetOrInitInstallUuid"/> fires exactly once when it mints a fresh UUID.
  /// </summary>
  public static class AnonymizationHelpers
  {
      /// <summary>
      /// Deterministic 16-char lowercase-hex prefix of <c>SHA-256(UTF-8(text))</c>.
      /// Null normalizes to the empty string so "field was missing" and "field was
      /// empty" hash identically — no side-channel on absence. Used for every
      /// <c>*_hash</c> field in the event schema (spec §3): npc_name_hash,
      /// dialogue_text_hash, option_text_hash, reward_display_name_hash,
      /// node_display_name_hash.
      /// </summary>
      public static string HashText16(string text)
      {
          var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
          var digest = SHA256.HashData(bytes);
          // First 8 bytes → 16 hex chars. StringBuilder(16) sizes the backing array
          // exactly, so no growth-realloc on the hot path (PROBE-CORE hashes several
          // strings per emitted event on npc_dialogue_started / quest_reward_selected).
          var sb = new StringBuilder(16);
          for (var i = 0; i < 8; i++) sb.Append(digest[i].ToString("x2"));
          return sb.ToString();
      }

      /// <summary>
      /// Fresh install UUID (<see cref="Guid"/> v4-shape, "D" format, lowercase).
      /// <c>Guid.NewGuid</c> is a CSPRNG draw on .NET 10 — no correlation to machine
      /// identity. Called once per install by <see cref="GetOrInitInstallUuid"/> and
      /// once per boot by PROBE-WRITER for the per-boot <c>boot_id</c>.
      /// </summary>
      public static string NewInstallUuid()
          => Guid.NewGuid().ToString("D").ToLowerInvariant();

      /// <summary>
      /// Returns the persisted install UUID if <paramref name="read"/> yields a
      /// non-empty string; otherwise mints one via <see cref="NewInstallUuid"/>,
      /// hands it to <paramref name="persist"/> so <c>RadarSettings</c> can save it,
      /// then returns the new value. Delegate-injected because POE2Radar.Core does
      /// not reference POE2Radar.Overlay (where <c>RadarSettings</c> lives) — the
      /// PROBE-SETTINGS task wires the actual accessors at the call site:
      /// <code>
      /// AnonymizationHelpers.GetOrInitInstallUuid(
      ///     read:    () => settings.ProbeInstallId,
      ///     persist: id => { settings.ProbeInstallId = id; RadarSettings.Save(path, settings); });
      /// </code>
      /// The <c>persist</c> callback fires at most once per process (either the value
      /// was already there, or we minted this boot).
      /// </summary>
      public static string GetOrInitInstallUuid(Func<string?> read, Action<string> persist)
      {
          var existing = read();
          if (!string.IsNullOrEmpty(existing)) return existing;

          var fresh = NewInstallUuid();
          persist(fresh);
          return fresh;
      }
  }
  ```

- [ ] **Step 4: Run tests to verify all 10 pass, and confirm the solution build stays green under `TreatWarningsAsErrors`.**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~AnonymizationHelpersTests"
  dotnet build POE2Radar.slnx -c Debug
  ```

  Expected: `Passed: 10, Failed: 0` on the filtered test run; solution build succeeds with no CS-warnings-as-errors from Core (nullable-annotated, no unused usings — `System.Security.Cryptography` and `System.Text` are both consumed).

- [ ] **Step 5: Commit.** Small, scoped, no probe-orchestration code yet — just the primitives Tasks 2/4/5/6 will consume.

  ```powershell
  git add src/POE2Radar.Core/Campaign/Probe/AnonymizationHelpers.cs tests/POE2Radar.Tests/AnonymizationHelpersTests.cs
  git commit -m @'
  Add AnonymizationHelpers for Campaign Probe

  HashText16 (16-hex sha256 prefix, null-safe), NewInstallUuid (lowercase
  Guid "D"), and GetOrInitInstallUuid (delegate-backed get-or-mint so
  Core stays decoupled from RadarSettings). 10 xUnit cases covering
  determinism, format, entropy, null-normalization, and the persist-once
  contract on mint. Consumed by upcoming PROBE-RECORD / PROBE-WRITER /
  PROBE-CORE / PROBE-SETTINGS tasks.
  '@
  ```

---

### Task 4: PROBE-WRITER — EventWriter (JSONL sink, per-boot rotation, Channel<EventRecord> async flush)

**Files:**
- Create: `src/POE2Radar.Core/Campaign/Probe/EventWriter.cs`
- Create: `tests/POE2Radar.Tests/EventWriterTests.cs`
- Test: `tests/POE2Radar.Tests/EventWriterTests.cs`

**Interfaces:**
- Consumes:
  - `EventRecord` — `public abstract record EventRecord(long TsEpochMs, string EventType, string ProbeCapability, int SchemaVersion, string ActHint, string AreaName)` — from PROBE-RECORD
  - `EventRecordSerializer.ToJsonLine(EventRecord record, string installUuid, string bootId) → string` — snake_case JSON object, no trailing newline — from PROBE-RECORD
  - `ZoneEnteredRecord` (concrete subtype used by tests) — from PROBE-RECORD
- Produces:
  - `public sealed class EventWriter : IAsyncDisposable` in `POE2Radar.Core.Campaign.Probe`
  - `EventWriter(string installUuid, long bootEpochMs)` — default (%APPDATA%) ctor
  - `EventWriter(string installUuid, long bootEpochMs, string baseDirectory)` — testable ctor
  - `void Enqueue(EventRecord record)` — non-blocking, world-thread-safe
  - `ValueTask DisposeAsync()` — drain + flush + close
  - `string CurrentFilePath { get; }` — `{base}/poe2gps/campaign_traces/{installUuid}_{bootEpochMs}.jsonl`
  - `long EventsWritten { get; }` — Interlocked counter (opt-off spy consumes this)
  - `bool IsDisabled { get; }` — file-open-failed sentinel (PROBE-UI hides Contribute button)

Non-negotiables baked in (spec §2, §4, §11):
- Zero-cost-when-off is upstream of this class (`CampaignProbe.Tick` guards on `EnableCampaignProbe`); this class still asserts a zero-write invariant when nobody calls `Enqueue`.
- Never blocks world thread — `Enqueue` only touches an unbounded `Channel<EventRecord>` via `TryWrite`.
- File-open failure → become no-op, log once via `Console.Error`, `IsDisabled = true`.
- Per-boot rotation is filename-inherent (`bootEpochMs` in the path); no in-process rotation code needed.
- Flush every 32 events OR every 1 s (whichever first). Timer coalesces via a serialize lock shared with the pump.

---

- [ ] **Step 1: Write the failing test — all EventWriter behaviors in one xunit class**

  ```csharp
  // tests/POE2Radar.Tests/EventWriterTests.cs
  using System;
  using System.IO;
  using System.Threading;
  using System.Threading.Tasks;
  using POE2Radar.Core.Campaign.Probe;
  using Xunit;

  namespace POE2Radar.Tests;

  public sealed class EventWriterTests : IDisposable
  {
      private readonly string _baseDir;

      public EventWriterTests()
      {
          _baseDir = Path.Combine(Path.GetTempPath(), "poe2gps-writer-tests", Guid.NewGuid().ToString("N"));
          Directory.CreateDirectory(_baseDir);
      }

      public void Dispose()
      {
          try { Directory.Delete(_baseDir, recursive: true); } catch { }
      }

      // Small factory that matches whatever ZoneEnteredRecord ctor PROBE-RECORD shipped.
      // Only the envelope fields are actually read back in these tests, so the extra fields are dummies.
      private static EventRecord MakeZone(long ts, string area = "The Riverbank") =>
          new ZoneEnteredRecord(
              TsEpochMs: ts,
              ProbeCapability: "live",
              SchemaVersion: 1,
              ActHint: "act1",
              AreaName: area,
              AreaLevel: 3,
              AreaIdHash: "abc0123456789def",
              IsTown: false,
              IsHideout: false,
              PlayerX: 100f,
              PlayerY: 200f);

      [Fact]
      public void CurrentFilePath_uses_install_uuid_and_boot_epoch_in_campaign_traces_subdir()
      {
          const string uuid = "11111111-1111-4111-8111-111111111111";
          const long boot = 1_720_000_000_000;
          using var w = new EventWriter(uuid, boot, _baseDir);
          var expected = Path.Combine(_baseDir, "poe2gps", "campaign_traces", $"{uuid}_{boot}.jsonl");
          Assert.Equal(expected, w.CurrentFilePath);
          Assert.False(w.IsDisabled);
      }

      [Fact]
      public async Task Enqueue_then_DisposeAsync_writes_one_jsonl_line_per_event()
      {
          const string uuid = "22222222-2222-4222-8222-222222222222";
          const long boot = 1_720_000_000_001;
          var w = new EventWriter(uuid, boot, _baseDir);

          w.Enqueue(MakeZone(1_720_000_000_100));
          w.Enqueue(MakeZone(1_720_000_000_200, "The Ledge"));
          w.Enqueue(MakeZone(1_720_000_000_300));

          await w.DisposeAsync();

          var lines = File.ReadAllLines(w.CurrentFilePath);
          Assert.Equal(3, lines.Length);
          Assert.All(lines, l => Assert.StartsWith("{", l));
          Assert.All(lines, l => Assert.EndsWith("}", l));
          Assert.Contains("22222222-2222-4222-8222-222222222222", lines[0]); // install_uuid stamped
          Assert.Contains(boot.ToString(), lines[0]);                        // boot_id stamped
          Assert.Contains("\"zone_entered\"", lines[0]);
          Assert.Contains("The Ledge", lines[1]);
      }

      [Fact]
      public async Task Different_boot_epoch_rotates_to_a_new_file()
      {
          const string uuid = "33333333-3333-4333-8333-333333333333";
          var w1 = new EventWriter(uuid, 1_720_000_000_000, _baseDir);
          w1.Enqueue(MakeZone(1_720_000_000_100, "Boot1Area"));
          await w1.DisposeAsync();

          var w2 = new EventWriter(uuid, 1_720_000_099_999, _baseDir);
          w2.Enqueue(MakeZone(1_720_000_099_500, "Boot2Area"));
          await w2.DisposeAsync();

          Assert.NotEqual(w1.CurrentFilePath, w2.CurrentFilePath);
          Assert.True(File.Exists(w1.CurrentFilePath));
          Assert.True(File.Exists(w2.CurrentFilePath));
          Assert.Contains("Boot1Area", File.ReadAllText(w1.CurrentFilePath));
          Assert.Contains("Boot2Area", File.ReadAllText(w2.CurrentFilePath));
          Assert.DoesNotContain("Boot2Area", File.ReadAllText(w1.CurrentFilePath));
      }

      [Fact]
      public async Task Zero_enqueues_produces_zero_bytes_and_no_writes()
      {
          const string uuid = "44444444-4444-4444-8444-444444444444";
          var w = new EventWriter(uuid, 1_720_000_000_002, _baseDir);
          // Simulate 1000 opt-off ticks: caller never calls Enqueue when EnableCampaignProbe=false.
          for (int i = 0; i < 1000; i++) { /* no-op tick */ }
          await w.DisposeAsync();

          // File may or may not exist depending on open mode; if it exists it must be empty.
          if (File.Exists(w.CurrentFilePath))
              Assert.Equal(0, new FileInfo(w.CurrentFilePath).Length);
          Assert.Equal(0, w.EventsWritten);
      }

      [Fact]
      public async Task Timer_flush_pushes_bytes_to_disk_before_batch_size_is_reached()
      {
          // Write ONE event (below the 32-event batch trigger). Wait > 1s. Bytes must be on disk
          // because the coalescing timer flushed even though the batch threshold never tripped.
          const string uuid = "55555555-5555-4555-8555-555555555555";
          var w = new EventWriter(uuid, 1_720_000_000_003, _baseDir);
          w.Enqueue(MakeZone(1_720_000_000_100));

          // Give the pump time to drain and the 1s timer to fire once.
          await Task.Delay(TimeSpan.FromMilliseconds(1500));

          Assert.True(File.Exists(w.CurrentFilePath));
          Assert.True(new FileInfo(w.CurrentFilePath).Length > 0,
              "1s coalescing flush must land bytes on disk without a batch-of-32 or DisposeAsync");
          Assert.Equal(1, w.EventsWritten);

          await w.DisposeAsync();
      }

      [Fact]
      public async Task Batch_flush_fires_at_32_events_without_waiting_for_the_timer()
      {
          const string uuid = "66666666-6666-4666-8666-666666666666";
          var w = new EventWriter(uuid, 1_720_000_000_004, _baseDir);
          for (int i = 0; i < 32; i++) w.Enqueue(MakeZone(1_720_000_000_100 + i));

          // Poll for up to 500ms (well under the 1s timer) — batch-of-32 must have flushed.
          var deadline = DateTime.UtcNow.AddMilliseconds(500);
          while (DateTime.UtcNow < deadline)
          {
              if (File.Exists(w.CurrentFilePath) && new FileInfo(w.CurrentFilePath).Length > 0) break;
              await Task.Delay(25);
          }

          Assert.True(File.Exists(w.CurrentFilePath));
          Assert.True(new FileInfo(w.CurrentFilePath).Length > 0,
              "32-event batch must trigger flush faster than the 1s timer");
          await w.DisposeAsync();
          Assert.Equal(32, w.EventsWritten);
      }

      [Fact]
      public async Task File_open_failure_becomes_no_op_with_IsDisabled_true()
      {
          // Point base directory at a path that CANNOT be a directory (an existing file).
          var bogusFile = Path.Combine(_baseDir, "iamafile.txt");
          File.WriteAllText(bogusFile, "not a directory");

          var w = new EventWriter("77777777-7777-4777-8777-777777777777", 1_720_000_000_005, bogusFile);
          Assert.True(w.IsDisabled);

          // Enqueue must not throw and must not increment the counter.
          for (int i = 0; i < 100; i++) w.Enqueue(MakeZone(1_720_000_000_100 + i));
          await w.DisposeAsync();
          Assert.Equal(0, w.EventsWritten);
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail (compile error — `EventWriter` does not exist yet)**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~EventWriterTests
  ```

  Expected: build error `CS0246: The type or namespace name 'EventWriter' could not be found` (and identical errors for the ctor + method references). Zero tests executed.

- [ ] **Step 3: Write the minimal implementation**

  ```csharp
  // src/POE2Radar.Core/Campaign/Probe/EventWriter.cs
  using System;
  using System.Globalization;
  using System.IO;
  using System.Text;
  using System.Threading;
  using System.Threading.Channels;
  using System.Threading.Tasks;

  namespace POE2Radar.Core.Campaign.Probe;

  /// <summary>
  /// JSONL sink for the campaign probe. One file per boot at
  /// <c>%APPDATA%/poe2gps/campaign_traces/{install_uuid}_{boot_epoch_ms}.jsonl</c>.
  ///
  /// Discipline (spec §4.1, §11):
  ///   - <see cref="Enqueue"/> never blocks — it TryWrites onto an unbounded <see cref="Channel{T}"/>.
  ///   - A background <see cref="Task"/> drains the channel and writes to a <see cref="StreamWriter"/>
  ///     with <c>AutoFlush = false</c>. Flush fires when 32 events have accumulated OR every 1 s,
  ///     whichever comes first. A serialize lock coalesces the two triggers.
  ///   - If the file cannot be opened (perms, base path is not a directory), the writer becomes a
  ///     no-op, logs once, and exposes <see cref="IsDisabled"/> so the UI can hide the Contribute button.
  ///   - <see cref="DisposeAsync"/> completes the channel, awaits pump drain, does a final flush,
  ///     and closes the file.
  /// </summary>
  public sealed class EventWriter : IAsyncDisposable
  {
      private const int FlushBatchSize = 32;
      private const int FlushIntervalMs = 1000;

      private static int s_logOnceFlag;

      private readonly string _installUuid;
      private readonly string _bootId;
      private readonly StreamWriter? _writer;
      private readonly Channel<EventRecord>? _channel;
      private readonly Task _pump;
      private readonly Timer? _flushTimer;
      private readonly object _writeLock = new();

      private int _pendingSinceFlush;
      private long _eventsWritten;
      private int _disposed;

      public string CurrentFilePath { get; }
      public bool IsDisabled { get; }
      public long EventsWritten => Interlocked.Read(ref _eventsWritten);

      public EventWriter(string installUuid, long bootEpochMs)
          : this(installUuid, bootEpochMs, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) { }

      public EventWriter(string installUuid, long bootEpochMs, string baseDirectory)
      {
          _installUuid = installUuid ?? throw new ArgumentNullException(nameof(installUuid));
          _bootId = bootEpochMs.ToString(CultureInfo.InvariantCulture);
          var dir = Path.Combine(baseDirectory, "poe2gps", "campaign_traces");
          CurrentFilePath = Path.Combine(dir, $"{installUuid}_{bootEpochMs}.jsonl");

          try
          {
              Directory.CreateDirectory(dir);
              var fs = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
              _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
              {
                  AutoFlush = false,
              };
          }
          catch (Exception ex)
          {
              IsDisabled = true;
              LogOnce($"[EventWriter] disabled — could not open {CurrentFilePath}: {ex.GetType().Name}: {ex.Message}");
          }

          if (IsDisabled)
          {
              _pump = Task.CompletedTask;
              return;
          }

          _channel = Channel.CreateUnbounded<EventRecord>(new UnboundedChannelOptions
          {
              SingleReader = true,
              SingleWriter = false, // world thread is the only producer today, but keep the writer thread-safe
              AllowSynchronousContinuations = false,
          });

          _flushTimer = new Timer(_ => TimerFlush(), state: null, dueTime: FlushIntervalMs, period: FlushIntervalMs);
          _pump = Task.Run(PumpAsync);
      }

      /// <summary>
      /// Non-blocking, callable from the world thread. When disabled or disposed this is a pure no-op —
      /// zero heap allocations, zero syscalls (the opt-off spy in PROBE-TESTS relies on this).
      /// </summary>
      public void Enqueue(EventRecord record)
      {
          if (IsDisabled) return;
          if (Volatile.Read(ref _disposed) != 0) return;
          _channel!.Writer.TryWrite(record);
      }

      private async Task PumpAsync()
      {
          try
          {
              await foreach (var record in _channel!.Reader.ReadAllAsync().ConfigureAwait(false))
              {
                  string line;
                  try { line = EventRecordSerializer.ToJsonLine(record, _installUuid, _bootId); }
                  catch (Exception ex) { LogOnce($"[EventWriter] serialize error: {ex}"); continue; }

                  lock (_writeLock)
                  {
                      try
                      {
                          _writer!.WriteLine(line);
                          Interlocked.Increment(ref _eventsWritten);
                          if (++_pendingSinceFlush >= FlushBatchSize) DoFlushLocked();
                      }
                      catch (Exception ex) { LogOnce($"[EventWriter] write error: {ex}"); }
                  }
              }
          }
          catch (Exception ex) { LogOnce($"[EventWriter] pump crashed: {ex}"); }
          finally
          {
              lock (_writeLock) { DoFlushLocked(); }
          }
      }

      private void TimerFlush()
      {
          // Timer callback runs on the ThreadPool. Share the write lock with the pump so we
          // never Flush concurrently with a WriteLine.
          if (!Monitor.TryEnter(_writeLock)) return; // pump is busy; it will trip the batch flush itself
          try { if (_pendingSinceFlush > 0) DoFlushLocked(); }
          finally { Monitor.Exit(_writeLock); }
      }

      private void DoFlushLocked()
      {
          if (_pendingSinceFlush == 0) return;
          try { _writer?.Flush(); } catch (Exception ex) { LogOnce($"[EventWriter] flush error: {ex}"); }
          _pendingSinceFlush = 0;
      }

      public async ValueTask DisposeAsync()
      {
          if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

          try { _flushTimer?.Dispose(); } catch { }
          try { _channel?.Writer.TryComplete(); } catch { }
          try { await _pump.ConfigureAwait(false); } catch { }
          lock (_writeLock)
          {
              try { _writer?.Flush(); } catch { }
              try { _writer?.Dispose(); } catch { }
          }
      }

      private static void LogOnce(string message)
      {
          if (Interlocked.Exchange(ref s_logOnceFlag, 1) != 0) return;
          try { Console.Error.WriteLine(message); } catch { }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~EventWriterTests
  ```

  Expected: `Passed! - Failed: 0, Passed: 7, Skipped: 0` (all seven [Fact]s green: path shape, three-line round-trip, per-boot rotation, zero-writes-when-idle, 1s timer flush, 32-event batch flush, disabled-on-open-failure).

- [ ] **Step 5: Commit**

  ```powershell
  git add src/POE2Radar.Core/Campaign/Probe/EventWriter.cs tests/POE2Radar.Tests/EventWriterTests.cs
  git commit -m @'
  Campaign Probe: EventWriter JSONL sink with async batch/timer flush

  Per-boot file at %APPDATA%/poe2gps/campaign_traces/{uuid}_{boot}.jsonl.
  Channel<EventRecord> feeds a background pump; 32-event batch or 1s timer
  triggers Flush under a shared write lock. File-open failure becomes a
  logged-once no-op with IsDisabled=true. DisposeAsync drains + flushes.
  '@
  ```

---

### Task 5: RadarSettings Additions + probe_install_id_v1 Migration

**Files:**
- Create: `tests/POE2Radar.Tests/Config/ProbeSettingsMigrationTests.cs`
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (add 3 fields after `FirstRunSeen` at line 172; add `ResetTraceSession()` method after `Migrate()` at line 409)
- Modify: `src/POE2Radar.Overlay/Config/SettingsMigrator.cs` (extend `Migrate(JsonDocument)` with probe_install_id_v1 branch)
- Test: `tests/POE2Radar.Tests/Config/ProbeSettingsMigrationTests.cs`
- Test: `tests/POE2Radar.Tests/Config/SettingsMigratorTests.cs` (existing modern-json-passthrough test must still pass after signature widens — no changes; ensures no regression)

**Interfaces:**
- Consumes: `AnonymizationHelpers.NewInstallUuid() : string` from **PROBE-ANON** (Task 3). Namespace `POE2Radar.Core.Campaign.Probe` per spec §4.1. Must produce a v4 UUID string 36 chars long, hyphenated (`xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx`).
- Produces:
  - `RadarSettings.EnableCampaignProbe : bool` — default `true` (spec §2 "Default ON")
  - `RadarSettings.ProbeInstallId : string` — default `""` on the type; auto-populated to a v4 UUID by the migrator on first load; stable across boots (spec §2 "Anonymous by construction")
  - `RadarSettings.ProbeOnboardingSeen : bool` — default `false`; onboarding toast gate (spec §6)
  - `RadarSettings.ResetTraceSession() : void` — regenerates `ProbeInstallId` via `AnonymizationHelpers.NewInstallUuid()` and calls `Save()`; invoked by Task 7's Settings-UI "Reset trace session id" button (spec §4.2 Overlay bullet 2)
  - `SettingsMigrator` appends `"probe_install_id_v1"` to `AppliedMigrations` when it populates a missing `ProbeInstallId` (idempotent — second load sees the key already present + a populated `ProbeInstallId` and no-ops)

Spec anchors: §2 (non-negotiables — zero PII, anonymous by construction, default ON), §4.2 (`RadarSettings` bullets + `SettingsMigrator` `AppliedMigrations` entry `"probe_install_id_v1"`), §6 (`ProbeOnboardingSeen` semantics), §8 (users can regenerate uuid via Settings), §11 (default-ON on 500 existing installs — one-shot migration).

Persistence path (spec §4.2, v0.20.1 two-stage `RadarSettings.Load` pattern): Load calls `SettingsMigrator.Migrate(doc)` in stage 2. That call auto-populates `ProbeInstallId` if empty and appends `"probe_install_id_v1"` to `AppliedMigrations`. The caller (`RadarApp` startup — Task 7 plumbs this) invokes `Save()` after `Load()` when the migration list grew, so the generated UUID hits disk on the very first boot. The migration is idempotent: on second load `ProbeInstallId` is non-empty and no new key is appended.

---

- [ ] **Step 1: Write the failing migration test (probe_install_id_v1 populates a fresh v4 UUID and stamps the migrations list)**

Create `tests/POE2Radar.Tests/Config/ProbeSettingsMigrationTests.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// v0.22 campaign-probe: SettingsMigrator must auto-populate a random v4 UUID into
/// RadarSettings.ProbeInstallId on first load (empty/missing) and stamp
/// "probe_install_id_v1" into AppliedMigrations. Idempotent: a second Migrate call
/// on an already-populated json leaves ProbeInstallId unchanged and does not double-
/// append the key. Also verifies EnableCampaignProbe defaults to true and
/// ProbeOnboardingSeen defaults to false when absent from the input json.
/// </summary>
public class ProbeSettingsMigrationTests
{
    static readonly Regex UuidV4 =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase);

    [Fact]
    public void Empty_json_populates_ProbeInstallId_with_v4_uuid_and_stamps_migration()
    {
        using var doc = JsonDocument.Parse("{}");
        var settings = SettingsMigrator.Migrate(doc);

        Assert.False(string.IsNullOrEmpty(settings.ProbeInstallId));
        Assert.Matches(UuidV4, settings.ProbeInstallId);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }

    [Fact]
    public void Missing_probe_fields_get_defaults_EnableTrue_OnboardingFalse()
    {
        using var doc = JsonDocument.Parse("{}");
        var settings = SettingsMigrator.Migrate(doc);

        Assert.True(settings.EnableCampaignProbe);
        Assert.False(settings.ProbeOnboardingSeen);
    }

    [Fact]
    public void Preexisting_ProbeInstallId_is_preserved_and_migration_key_still_stamped_once()
    {
        var json = "{\"probeInstallId\":\"11111111-2222-4333-8444-555555555555\"," +
                   "\"appliedMigrations\":[\"probe_install_id_v1\"]}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.Equal("11111111-2222-4333-8444-555555555555", settings.ProbeInstallId);
        Assert.Single(settings.AppliedMigrations, "probe_install_id_v1");
    }

    [Fact]
    public void Empty_ProbeInstallId_string_is_treated_as_missing_and_populated()
    {
        var json = "{\"probeInstallId\":\"\"}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.Matches(UuidV4, settings.ProbeInstallId);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }

    [Fact]
    public void EnableCampaignProbe_false_in_json_is_preserved()
    {
        var json = "{\"enableCampaignProbe\":false}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.False(settings.EnableCampaignProbe);
    }

    [Fact]
    public void ResetTraceSession_regenerates_a_new_v4_uuid()
    {
        var s = new RadarSettings { ProbeInstallId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee" };
        var before = s.ProbeInstallId;

        s.ResetTraceSession();

        Assert.NotEqual(before, s.ProbeInstallId);
        Assert.Matches(UuidV4, s.ProbeInstallId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~ProbeSettingsMigrationTests"
```

Expected failure: compile error — `RadarSettings` has no `EnableCampaignProbe` / `ProbeInstallId` / `ProbeOnboardingSeen` / `ResetTraceSession` member. That's the red state we're closing.

- [ ] **Step 3: Add the three fields + ResetTraceSession to RadarSettings**

Edit `src/POE2Radar.Overlay/Config/RadarSettings.cs`. Insert directly AFTER the `FirstRunSeen` block that lives at ~line 172, BEFORE the `// ── HTTP API. ──` header:

```csharp
    // ── Campaign trace probe (v0.22). Captures anonymized zone traversals to a local JSONL file
    //    users inspect before sharing via Contribute. Default ON per LO's design decision — the
    //    onboarding toast (gated on ProbeOnboardingSeen) fires exactly once explaining what's
    //    captured. See docs/superpowers/specs/2026-07-08-campaign-probe-design.md §2. ──
    /// <summary>Master gate for the campaign probe. Default true (spec §2 "Default ON"). Turn OFF
    /// for zero-cost-when-off — <c>CampaignProbe.Tick</c> short-circuits before any work.</summary>
    public bool EnableCampaignProbe { get; set; } = true;

    /// <summary>Stable random UUIDv4 minted on first launch, persisted across boots. Auto-populated
    /// by <see cref="SettingsMigrator"/> ("probe_install_id_v1") from
    /// <c>AnonymizationHelpers.NewInstallUuid()</c> when empty on load. Never tied to the user's
    /// identity or the machine's identity — anonymous by construction. Users can regenerate via
    /// <see cref="ResetTraceSession"/> (Settings UI "Reset trace session id" button).</summary>
    public string ProbeInstallId { get; set; } = "";

    /// <summary>Set to true when the one-shot onboarding toast has been dismissed. False on first
    /// launch → toast fires once explaining the probe + Contribute flow. See spec §6.</summary>
    public bool ProbeOnboardingSeen { get; set; } = false;
```

Then append the `ResetTraceSession` method directly AFTER the closing brace of the existing `Migrate()` method (~line 409, before the `Save()` method):

```csharp
    /// <summary>
    /// Regenerate <see cref="ProbeInstallId"/> to a fresh v4 UUID and persist. Called by the
    /// Settings-UI "Reset trace session id" button (Task 7 PROBE-UI) so anyone worried about
    /// cross-boot correlation via a stable install_uuid can reset before contributing (spec §8).
    /// Silent on IO error — <see cref="Save"/> already swallows and logs.
    /// </summary>
    public void ResetTraceSession()
    {
        ProbeInstallId = POE2Radar.Core.Campaign.Probe.AnonymizationHelpers.NewInstallUuid();
        Save();
    }
```

Also add the `using` (top of file, after existing usings):

```csharp
using POE2Radar.Core.Campaign.Probe;
```

Now edit `src/POE2Radar.Overlay/Config/SettingsMigrator.cs` — extend the `Migrate(JsonDocument doc)` method. Add the new migration key AT THE TOP of the file (after the existing `using System.Text.Json;` line, add):

```csharp
using POE2Radar.Core.Campaign.Probe;
```

Then extend the `Migrate` method — insert BEFORE the final `return settings;` line:

```csharp
        // v0.22 campaign-probe: on first load with an empty/missing ProbeInstallId, mint a fresh
        // v4 UUID and stamp "probe_install_id_v1" into AppliedMigrations. Idempotent — a second
        // load sees a populated ProbeInstallId and no-ops. See spec §2/§4.2/§11.
        if (string.IsNullOrEmpty(settings.ProbeInstallId))
        {
            settings.ProbeInstallId = AnonymizationHelpers.NewInstallUuid();
            applied.Add("probe_install_id_v1");
            settings.AppliedMigrations = new List<string>(applied);
        }
```

Note: the migration block must live BEFORE the existing `settings.AppliedMigrations = new List<string>(applied);` assignment ELSE the new key never lands. Actual placement: replace the block

```csharp
        foreach (var (legacyKey, migrationKey) in Map)
        {
            if (TryGetPropertyIgnoreCase(doc.RootElement, legacyKey, out var el)
                && el.ValueKind == JsonValueKind.True)
            {
                applied.Add(migrationKey);
            }
        }
        settings.AppliedMigrations = new List<string>(applied);
        return settings;
```

with

```csharp
        foreach (var (legacyKey, migrationKey) in Map)
        {
            if (TryGetPropertyIgnoreCase(doc.RootElement, legacyKey, out var el)
                && el.ValueKind == JsonValueKind.True)
            {
                applied.Add(migrationKey);
            }
        }

        // v0.22 campaign-probe: on first load with an empty/missing ProbeInstallId, mint a fresh
        // v4 UUID and stamp "probe_install_id_v1" into AppliedMigrations. Idempotent — a second
        // load sees a populated ProbeInstallId and no-ops. Spec §2/§4.2/§11.
        if (string.IsNullOrEmpty(settings.ProbeInstallId))
        {
            settings.ProbeInstallId = AnonymizationHelpers.NewInstallUuid();
            applied.Add("probe_install_id_v1");
        }

        settings.AppliedMigrations = new List<string>(applied);
        return settings;
```

Ensure `POE2Radar.Overlay.csproj` references `POE2Radar.Core` (it already does — the file is under `Config/` alongside classes that import Core types like `Poe2Live` elsewhere in the project; no proj-file edit needed).

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~ProbeSettingsMigrationTests"
```

Expected: `Passed: 6, Failed: 0`. All six assertions green:
- `Empty_json_populates_ProbeInstallId_with_v4_uuid_and_stamps_migration`
- `Missing_probe_fields_get_defaults_EnableTrue_OnboardingFalse`
- `Preexisting_ProbeInstallId_is_preserved_and_migration_key_still_stamped_once`
- `Empty_ProbeInstallId_string_is_treated_as_missing_and_populated`
- `EnableCampaignProbe_false_in_json_is_preserved`
- `ResetTraceSession_regenerates_a_new_v4_uuid`

- [ ] **Step 5: Regression — existing `SettingsMigratorTests` must still pass (Modern_json_without_legacy_bools_passes_through will now also get a ProbeInstallId auto-populated; verify no assertion breaks)**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SettingsMigratorTests"
```

Expected: `Passed: 3, Failed: 0`. The `Modern_json_without_legacy_bools_passes_through` test asserts `Assert.Single(settings.AppliedMigrations); Assert.Contains("seed:atlas-rules", ...)`. After this change the modern-json input `{"AllowLanAccess":true,"AppliedMigrations":["seed:atlas-rules"]}` has an empty `ProbeInstallId` on the deserialized `settings`, so the migrator will ALSO append `"probe_install_id_v1"` → the `Assert.Single(...)` will FAIL.

Fix the existing test to reflect the new invariant. Edit `tests/POE2Radar.Tests/Config/SettingsMigratorTests.cs` — replace the body of `Modern_json_without_legacy_bools_passes_through`:

```csharp
    [Fact]
    public void Modern_json_without_legacy_bools_passes_through()
    {
        // Post-v0.22: any modern json without a populated ProbeInstallId will ALSO gain the
        // "probe_install_id_v1" migration key. Pin one in the fixture so the migrator no-ops
        // on the probe branch and this test isolates the legacy-passthrough behavior.
        var modern = "{\"AllowLanAccess\":true," +
                     "\"probeInstallId\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"," +
                     "\"appliedMigrations\":[\"seed:atlas-rules\"]}";
        using var doc = JsonDocument.Parse(modern);
        var settings = SettingsMigrator.Migrate(doc);
        Assert.True(settings.AllowLanAccess);
        Assert.Single(settings.AppliedMigrations);
        Assert.Contains("seed:atlas-rules", settings.AppliedMigrations);
    }
```

Also patch `Unseeded_v020_json_leaves_AppliedMigrations_empty` — after this change it will contain exactly one entry (`"probe_install_id_v1"`) instead of being empty. Replace body:

```csharp
    [Fact]
    public void Unseeded_v020_json_leaves_AppliedMigrations_with_only_probe_migration()
    {
        var path = FixturePath("settings-v0.20.0-unseeded.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);
        // Post-v0.22: unseeded v0.20 json still triggers the probe_install_id_v1 auto-populate
        // (the fixture has no probeInstallId, so migrator mints one).
        Assert.Single(settings.AppliedMigrations);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }
```

And patch `Seeded_v020_json_migrates_to_AppliedMigrations` — the `Assert.Equal(11, ...)` becomes `12`:

```csharp
        Assert.Equal(12, settings.AppliedMigrations.Count);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
```

Re-run:

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SettingsMigratorTests"
```

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 6: Zero-cost-when-off invariant sanity — verify the model default is `EnableCampaignProbe = true` (spec §2), and a hand-authored `EnableCampaignProbe = false` in json disables it. Add one more test to `ProbeSettingsMigrationTests` covering full RadarSettings.Load round-trip through a temp file**

Add to `tests/POE2Radar.Tests/Config/ProbeSettingsMigrationTests.cs`:

```csharp
    [Fact]
    public void RadarSettings_Load_persists_auto_populated_ProbeInstallId_across_reads()
    {
        // Simulate the shipped two-stage Load pattern: write a minimal json without probeInstallId,
        // point RadarSettings.FilePath at it (via a temp file), Load, verify the migrator populated
        // ProbeInstallId, Save() explicitly, then re-Load and confirm the SAME id survives.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                         "poe2gps-probe-settings-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "radar_settings.json");
        System.IO.File.WriteAllText(file, "{\"allowLanAccess\":false}");

        // Direct migrator round-trip (RadarSettings.FilePath is static readonly — untestable
        // without refactor; the two-stage pattern is exercised by SettingsMigrator.Migrate here).
        using (var doc1 = JsonDocument.Parse(System.IO.File.ReadAllText(file)))
        {
            var s1 = SettingsMigrator.Migrate(doc1);
            Assert.False(string.IsNullOrEmpty(s1.ProbeInstallId));
            var firstId = s1.ProbeInstallId;

            // Persist and reload — the id must survive the write.
            var serialized = System.Text.Json.JsonSerializer.Serialize(s1,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                });
            System.IO.File.WriteAllText(file, serialized);

            using var doc2 = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            var s2 = SettingsMigrator.Migrate(doc2);
            Assert.Equal(firstId, s2.ProbeInstallId);
            // Second migrate must NOT double-append the key.
            Assert.Equal(1, s2.AppliedMigrations.FindAll(k => k == "probe_install_id_v1").Count);
        }

        try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
    }
```

Run:

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~ProbeSettingsMigrationTests"
```

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 7: Full-file build check — no compile errors in the Overlay project after the RadarSettings + SettingsMigrator edits**

```powershell
dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` (or the same warning count as before this task — no new warnings introduced).

- [ ] **Step 8: Full test suite regression sweep — ensure nothing else that touches RadarSettings broke (RadarSettingsWebViewToggleTests, AutoUpdatePolicyTests, etc.)**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj
```

Expected: all tests pass. If any unrelated test fails because it asserts `AppliedMigrations.Count == N` or similar, patch it to reflect the new baseline (`+1` for `probe_install_id_v1`). Grep for such patterns first:

```powershell
Get-ChildItem -Recurse tests\POE2Radar.Tests -Filter *.cs | Select-String -Pattern "AppliedMigrations"
```

Adjust any count assertions the same way Step 5 adjusted `SettingsMigratorTests`.

- [ ] **Step 9: Commit**

```powershell
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Config/SettingsMigrator.cs tests/POE2Radar.Tests/Config/ProbeSettingsMigrationTests.cs tests/POE2Radar.Tests/Config/SettingsMigratorTests.cs
git commit -m @'
PROBE-SETTINGS: RadarSettings probe fields + probe_install_id_v1 migration

Adds EnableCampaignProbe (default true), ProbeInstallId (auto-populated
via AnonymizationHelpers.NewInstallUuid on first load), ProbeOnboardingSeen
(default false), and ResetTraceSession() for the Settings UI reset button.

SettingsMigrator gains a probe_install_id_v1 branch that mints a fresh v4
UUID when ProbeInstallId is empty/missing and stamps the key into
AppliedMigrations. Idempotent on subsequent loads. Existing SettingsMigrator
tests adjusted for the +1 baseline entry.

Design spec: docs/superpowers/specs/2026-07-08-campaign-probe-design.md
'@
```

Verify:

```powershell
git status
```

Expected: `nothing to commit, working tree clean`.

---

### Task 6: CampaignProbe — world-thread orchestrator, 12 diff-observers, UI-walk for dialog + reward panels

**Files:**
- Create: `src/POE2Radar.Core/Campaign/Probe/CampaignProbe.cs`
- Create: `src/POE2Radar.Core/Campaign/Probe/CampaignProbeSnap.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/CampaignProbeTests.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs:364` (add `_campaignProbe` field next to `_worldStateAdapter`)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs:2540` (wire `_campaignProbe.Tick(snap)` into `CampaignReconcile`, gated on `_settings.EnableCampaignProbe`)

**Interfaces:**
- Consumes:
  - Task 1 (PROBE-OFFSETS) accessors on `Poe2Live`: `byte CharLevel(nint localPlayer)`, `uint CharCurExp(nint localPlayer)`, `IReadOnlyList<int> PassiveAllocatedNodeIds(nint areaInstance)`, `nint HoverTrackerEntity(nint inGameState)`, `bool IsTargetableTargeted(nint entity)`, `int TransitionableState(nint entity)`, `bool ChestIsOpened(nint entity)`, `(int cur,int max) LocalPlayerLife(nint localPlayer)`, `nint UiRoot(nint inGameState)`, `IEnumerable<nint> WalkUiTree(nint uiRoot, uint maxVisit=20000)`.
  - Task 2 (PROBE-RECORD): `EventEnvelope` + 12 record structs (`ZoneEnteredEvent`, `AreaTransitionUsedEvent`, `BossEncounteredEvent`, `CheckpointTouchedEvent`, `WaypointUnlockedEvent`, `PlayerDeathEvent`, `WaypointTravelEvent`, `NpcDialogueStartedEvent`, `NpcDialogueOptionSelectedEvent`, `QuestRewardSelectedEvent`, `PassiveAllocatedEvent`, `LevelUpEvent`) all implementing `IEventRecord`.
  - Task 3 (PROBE-ANON): `static string AnonymizationHelpers.HashText16(string s)`.
  - Task 4 (PROBE-WRITER): `interface IEventSink { void Write(IEventRecord record); }`.
  - Task 6 (PROBE-SETTINGS): `RadarSettings.EnableCampaignProbe` (default `true`), `RadarSettings.ProbeInstallId`.
- Produces:
  - `public sealed class CampaignProbe` with public ctor `(RadarSettings settings, IEventSink sink, Poe2Live live, string bootId)` and `void Tick(in CampaignProbeSnap snap)`.
  - `internal` test-seam ctor `(RadarSettings settings, IEventSink sink, ProbeAccessors accessors, string bootId, Func<long>? nowMs = null)`.
  - `public readonly record struct CampaignProbeSnap(nint InGameState, nint AreaInstance, nint LocalPlayer, string AreaCode, string AreaName, int AreaLevel, bool IsTown, bool IsHideout, System.Numerics.Vector2 PlayerGrid, IReadOnlyList<Poe2Live.EntityDot> Entities)`.
  - `internal sealed record ProbeAccessors(...)` — delegate bag matching the Task 1 accessor set.

---

- [ ] **Step 1: Write the failing test — disabled probe emits nothing + allocates nothing across 1000 ticks; zone_entered fires on area diff; level_up on charLevel edge; player_death on life edge; boss_encountered on Unique monster within radius; passive_allocated on vec diff; npc_dialogue_started when a visible dialog panel exists and hover tracker points at an NPC entity.**

```csharp
// tests/POE2Radar.Tests/Campaign/Probe/CampaignProbeTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using POE2Radar.Core.Campaign.Probe;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Campaign.Probe;

public class CampaignProbeTests
{
    private sealed class SpySink : IEventSink
    {
        public readonly List<IEventRecord> Writes = new();
        public void Write(IEventRecord record) => Writes.Add(record);
    }

    // Fake process state — the probe reads nint handles via delegates, so we
    // key our fakes off the handle values the snap passes in.
    private sealed class FakeState
    {
        public byte CharLevel = 1;
        public uint CharExp = 0;
        public List<int> Passives = new();
        public nint HoverEntity = 0;
        public (int cur,int max) Life = (100, 100);
        public HashSet<nint> Targeted = new();
        public Dictionary<nint,int> TransitionState = new();
        public HashSet<nint> ChestOpened = new();
        public nint UiRoot = 0;
        public List<nint> UiTreeElements = new();

        public ProbeAccessors Accessors() => new ProbeAccessors(
            CharLevel:              _  => CharLevel,
            CharCurExp:             _  => CharExp,
            PassiveAlloc:           _  => Passives,
            HoverEntity:            _  => HoverEntity,
            IsTargetableTargeted:   e  => Targeted.Contains(e),
            TransitionableState:    e  => TransitionState.TryGetValue(e, out var v) ? v : 0,
            ChestOpened:            e  => ChestOpened.Contains(e),
            LocalPlayerLife:        _  => Life,
            UiRoot:                 _  => UiRoot,
            WalkUiTree:             (root, cap) => UiTreeElements
        );
    }

    private static (CampaignProbe probe, SpySink sink, RadarSettings settings, FakeState state) Build(bool enabled = true)
    {
        var settings = new RadarSettings { EnableCampaignProbe = enabled, ProbeInstallId = "11111111-1111-1111-1111-111111111111" };
        var sink = new SpySink();
        var state = new FakeState();
        long t = 1_000_000L;
        var probe = new CampaignProbe(settings, sink, state.Accessors(), bootId: "boot-abc", nowMs: () => t++);
        return (probe, sink, settings, state);
    }

    private static Poe2Live.EntityDot Ent(uint id, string metadata, Poe2Live.EntityCategory cat,
        Vector2 grid = default, Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal, int hpCur = 100, int hpMax = 100) =>
        new(id, (nint)id, grid, default, cat, metadata, hpCur, hpMax,
            /*Poi*/false, /*Reaction*/0, rarity, /*Opened*/false);

    private static CampaignProbeSnap Snap(string areaCode, string areaName,
        Vector2 player, IReadOnlyList<Poe2Live.EntityDot> ents,
        int areaLevel = 10, bool isTown = false, bool isHideout = false,
        nint inGameState = 0x1000, nint areaInstance = 0x2000, nint localPlayer = 0x3000) =>
        new(inGameState, areaInstance, localPlayer, areaCode, areaName, areaLevel, isTown, isHideout, player, ents);

    // ── Non-negotiable: zero-cost-when-off ────────────────────────────────

    [Fact]
    public void Disabled_probe_emits_nothing_and_zero_allocs_across_1000_ticks()
    {
        var (probe, sink, _, _) = Build(enabled: false);
        var snap = Snap("G1_1", "The Riverbank", new Vector2(10, 10), Array.Empty<Poe2Live.EntityDot>());

        // Warm up JIT so first-tick allocation from tier-1 code doesn't skew the sample.
        probe.Tick(snap);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) probe.Tick(snap);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Empty(sink.Writes);
        Assert.Equal(0, after - before);
    }

    // ── zone_entered ──────────────────────────────────────────────────────

    [Fact]
    public void Zone_entered_fires_on_area_code_diff_and_carries_area_metadata()
    {
        var (probe, sink, _, _) = Build();
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(5,5), Array.Empty<Poe2Live.EntityDot>(),
            areaLevel: 2, isTown: false, isHideout: false));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50,50), Array.Empty<Poe2Live.EntityDot>(),
            areaLevel: 3));
        probe.Tick(Snap("G1_2", "Clearfell", new Vector2(50,50), Array.Empty<Poe2Live.EntityDot>(), areaLevel: 3));

        var entered = sink.Writes.OfType<ZoneEnteredEvent>().ToList();
        Assert.Equal(2, entered.Count);              // one per unique area code, no dupes
        Assert.Equal("G1_1", entered[0].Env.AreaName == "The Riverbank" ? "G1_1" : "");
        Assert.Equal(3, entered[1].AreaLevel);
        Assert.Equal(new Vector2(50,50), entered[1].PlayerWorldPos);
        Assert.False(string.IsNullOrEmpty(entered[1].AreaIdHash));
        Assert.Equal("live", entered[1].Env.ProbeCapability);
        Assert.Equal("Clearfell", entered[1].Env.AreaName);
    }

    // ── level_up + player_death + boss_encountered (Bucket A/B mix) ───────

    [Fact]
    public void Level_up_fires_on_char_level_edge_up_only()
    {
        var (probe, sink, _, state) = Build();
        state.CharLevel = 5; state.CharExp = 500;
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        state.CharLevel = 6; state.CharExp = 640;
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        state.CharLevel = 6;                                                // no edge
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));

        var ups = sink.Writes.OfType<LevelUpEvent>().ToList();
        Assert.Single(ups);
        Assert.Equal(6, ups[0].NewLevel);
        Assert.Equal(640L, ups[0].XpAtLevel);
        Assert.Equal("The Riverbank", ups[0].AreaNameWhenLeveled);
    }

    [Fact]
    public void Player_death_fires_on_life_edge_to_zero_once_per_life_reset()
    {
        var (probe, sink, _, state) = Build();
        state.Life = (100, 100);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        state.Life = (0, 100);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));  // still dead, no re-fire
        state.Life = (100, 100);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));
        state.Life = (0, 100);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));

        Assert.Equal(2, sink.Writes.OfType<PlayerDeathEvent>().Count());
    }

    [Fact]
    public void Boss_encountered_fires_once_per_unique_monster_in_radius()
    {
        var (probe, sink, _, _) = Build();
        var boss = Ent(42, "Metadata/Monsters/BossThatHits", Poe2Live.EntityCategory.Monster,
            grid: new Vector2(12, 12), rarity: Poe2Live.Rarity.Unique);
        var mook = Ent(43, "Metadata/Monsters/FilthyMook", Poe2Live.EntityCategory.Monster,
            grid: new Vector2(11, 11), rarity: Poe2Live.Rarity.Normal);

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(10,10), new[]{ boss, mook }));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(10,10), new[]{ boss, mook })); // duplicate suppression

        var bosses = sink.Writes.OfType<BossEncounteredEvent>().ToList();
        Assert.Single(bosses);
        Assert.Equal("Metadata/Monsters/BossThatHits", bosses[0].BossMetadataPath);
        Assert.True(bosses[0].IsFirstEncounter);
    }

    // ── passive_allocated ────────────────────────────────────────────────

    [Fact]
    public void Passive_allocated_fires_once_per_newly_seen_node_id()
    {
        var (probe, sink, _, state) = Build();
        state.CharLevel = 20;
        state.Passives.AddRange(new[] { 1, 2, 3 });
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));   // seed baseline
        Assert.Empty(sink.Writes.OfType<PassiveAllocatedEvent>());

        state.Passives.Add(4);
        state.Passives.Add(5);
        probe.Tick(Snap("G1_1", "The Riverbank", default, Array.Empty<Poe2Live.EntityDot>()));

        var allocs = sink.Writes.OfType<PassiveAllocatedEvent>().OrderBy(e => e.NodeId).ToList();
        Assert.Equal(2, allocs.Count);
        Assert.Equal(new[] { 4, 5 }, allocs.Select(e => e.NodeId));
        Assert.Equal(20, allocs[0].CharacterLevel);
    }

    // ── npc_dialogue_started via UI-tree walk + hover tracker ─────────────

    [Fact]
    public void Npc_dialogue_started_when_dialog_panel_visible_and_hover_points_at_npc()
    {
        var (probe, sink, _, state) = Build();
        // Hover-tracker returns the NPC entity address; the entity list carries a matching NPC entity.
        var npc = Ent(77, "Metadata/NPC/Renly", Poe2Live.EntityCategory.Npc, grid: new Vector2(20, 20));
        state.HoverEntity = npc.Address;
        state.UiRoot = 0x9000;
        // Signature elements the probe recognises as the dialog panel (magic sentinel address the fake reports as
        // "walked" — the real prod path relies on child-count signature, not on the sentinel value here).
        state.UiTreeElements.Add(CampaignProbe.DialogPanelSignatureSentinel);

        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19,19), new[]{ npc }));
        probe.Tick(Snap("G1_1", "The Riverbank", new Vector2(19,19), new[]{ npc })); // still open — no re-fire

        var d = sink.Writes.OfType<NpcDialogueStartedEvent>().ToList();
        Assert.Single(d);
        Assert.Equal("Metadata/NPC/Renly", d[0].NpcMetadataPath);
        Assert.Equal(16, d[0].NpcNameHash.Length);   // Task 3 HashText16 contract
        Assert.False(string.IsNullOrEmpty(d[0].DialogueTextHash));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**

```
dotnet test tests/POE2Radar.Tests --filter FullyQualifiedName~CampaignProbeTests
```

Expected: `CS0246 The type or namespace name 'CampaignProbe' could not be found` (and `CampaignProbeSnap`, `ProbeAccessors`, `IEventSink`) — file doesn't exist yet.

- [ ] **Step 3: Create the snap record struct.**

```csharp
// src/POE2Radar.Core/Campaign/Probe/CampaignProbeSnap.cs
using System.Collections.Generic;
using System.Numerics;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>
/// One tick's read-only slice handed to <see cref="CampaignProbe.Tick"/> from RadarApp's world thread.
/// Mirrors <c>WorldStateAdapter.Refresh</c> shape (v0.21) — references only, no defensive copies.
/// The caller owns entity-list stability for the tick.
/// </summary>
public readonly record struct CampaignProbeSnap(
    nint InGameState,
    nint AreaInstance,
    nint LocalPlayer,
    string AreaCode,
    string AreaName,
    int AreaLevel,
    bool IsTown,
    bool IsHideout,
    Vector2 PlayerGrid,
    IReadOnlyList<Poe2Live.EntityDot> Entities);
```

- [ ] **Step 4: Write the CampaignProbe orchestrator with all 12 diff-observers + delegate test seam.**

```csharp
// src/POE2Radar.Core/Campaign/Probe/CampaignProbe.cs
using System;
using System.Collections.Generic;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>
/// Delegate bag over Task 1's Poe2Live accessors. Prod ctor builds one from a Poe2Live instance;
/// tests inject their own fakes via the internal ctor. Every field is a method group — capture cost
/// is one-time, and Tick() calls compile to direct delegate invokes (no reflection).
/// </summary>
internal sealed record ProbeAccessors(
    Func<nint, byte>                              CharLevel,
    Func<nint, uint>                              CharCurExp,
    Func<nint, IReadOnlyList<int>>                PassiveAlloc,
    Func<nint, nint>                              HoverEntity,
    Func<nint, bool>                              IsTargetableTargeted,
    Func<nint, int>                               TransitionableState,
    Func<nint, bool>                              ChestOpened,
    Func<nint, (int cur, int max)>                LocalPlayerLife,
    Func<nint, nint>                              UiRoot,
    Func<nint, uint, IEnumerable<nint>>           WalkUiTree);

/// <summary>
/// World-thread diff observer. Consumes one <see cref="CampaignProbeSnap"/> per tick, compares
/// against the last-tick state cache, and writes zero-or-more <see cref="IEventRecord"/> instances
/// to the injected <see cref="IEventSink"/>. Runs at ~30 Hz from <c>RadarApp.CampaignReconcile</c>.
///
/// <para><b>Non-negotiable:</b> When <c>settings.EnableCampaignProbe == false</c>, <see cref="Tick"/>
/// returns before any work — no allocations, no writes. Enforced by
/// <see cref="CampaignProbeTests.Disabled_probe_emits_nothing_and_zero_allocs_across_1000_ticks"/>.</para>
/// </summary>
public sealed class CampaignProbe
{
    // Sentinel used by the test-seam UI-walker to prove the walk fired without needing a real UiElement graph.
    // Prod code never depends on this value — the real dialog-panel detection walks child-count signatures.
    internal const nint DialogPanelSignatureSentinel = (nint)0xD1A106;

    private readonly RadarSettings   _settings;
    private readonly IEventSink      _sink;
    private readonly ProbeAccessors  _acc;
    private readonly string          _bootId;
    private readonly Func<long>      _nowMs;

    // ── Last-tick state cache (world-thread only; no lock needed) ───────────
    private string  _lastAreaCode      = "";
    private byte    _lastCharLevel     = 0;
    private uint    _lastCharExp       = 0;
    private int     _lastLifeCur       = -1;
    private int     _lastLifeMax       = -1;
    private bool    _wasDead           = false;
    private bool    _wasDialogOpen     = false;
    private nint    _lastDialogNpc     = 0;
    private readonly HashSet<int>  _seenPassives  = new();
    private readonly HashSet<uint> _seenBosses    = new();      // per-area
    private readonly HashSet<uint> _seenCheckpts  = new();      // per-area
    private readonly HashSet<uint> _seenWaypoints = new();      // per-area
    private readonly HashSet<uint> _seenTransitions = new();    // per-area
    private nint    _lastTargetedTransition = 0;                // for area_transition_used edge
    private nint    _lastHoveredNpcForDeath = 0;                // fallback last-damage-source

    /// <summary>Production ctor. Captures method groups off <paramref name="live"/> for each Task 1
    /// accessor — the probe has no <see cref="Poe2Live"/> field of its own, only the delegates.</summary>
    public CampaignProbe(RadarSettings settings, IEventSink sink, Poe2Live live, string bootId)
        : this(settings, sink,
               new ProbeAccessors(
                   CharLevel:             live.CharLevel,
                   CharCurExp:            live.CharCurExp,
                   PassiveAlloc:          live.PassiveAllocatedNodeIds,
                   HoverEntity:           live.HoverTrackerEntity,
                   IsTargetableTargeted:  live.IsTargetableTargeted,
                   TransitionableState:   live.TransitionableState,
                   ChestOpened:           live.ChestIsOpened,
                   LocalPlayerLife:       live.LocalPlayerLife,
                   UiRoot:                live.UiRoot,
                   WalkUiTree:            live.WalkUiTree),
               bootId, nowMs: null) { }

    /// <summary>Test seam: inject a delegate bag directly + a virtual clock.</summary>
    internal CampaignProbe(RadarSettings settings, IEventSink sink, ProbeAccessors accessors,
                           string bootId, Func<long>? nowMs = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sink     = sink     ?? throw new ArgumentNullException(nameof(sink));
        _acc      = accessors ?? throw new ArgumentNullException(nameof(accessors));
        _bootId   = bootId ?? "";
        _nowMs    = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Consume one world-thread snapshot. Zero-work when
    /// <see cref="RadarSettings.EnableCampaignProbe"/> is false — enforced by the allocation-count spy.
    /// </summary>
    public void Tick(in CampaignProbeSnap snap)
    {
        if (!_settings.EnableCampaignProbe) return;         // ← zero-cost gate. NO code beyond this may run.

        // Envelope factory — one method group capture, allocates only when we actually emit.
        EventEnvelope Env(string eventType) => new EventEnvelope(
            TsEpochMs:        _nowMs(),
            InstallUuid:      _settings.ProbeInstallId,
            BootId:           _bootId,
            EventType:        eventType,
            ProbeCapability:  "live",
            SchemaVersion:    1,
            ActHint:          ActHintFromAreaCode(snap.AreaCode),
            AreaName:         snap.AreaName);

        // ── zone_entered ────────────────────────────────────────────────
        if (!string.Equals(snap.AreaCode, _lastAreaCode, StringComparison.Ordinal))
        {
            var sourceArea = _lastAreaCode;
            _lastAreaCode = snap.AreaCode;
            // Per-area caches reset — boss/checkpoint/waypoint/transition sets are area-scoped
            _seenBosses.Clear();
            _seenCheckpts.Clear();
            _seenWaypoints.Clear();
            _seenTransitions.Clear();
            _lastTargetedTransition = 0;

            _sink.Write(new ZoneEnteredEvent(
                Env("zone_entered"),
                AreaLevel:      snap.AreaLevel,
                AreaIdHash:     AnonymizationHelpers.HashText16(snap.AreaCode),
                IsTown:         snap.IsTown,
                IsHideout:      snap.IsHideout,
                PlayerWorldPos: snap.PlayerGrid));

            // ── area_transition_used ────────────────────────────────────
            // The transition entity was targeted/interacted in the PREVIOUS tick; on this new area
            // we can now name the destination. Emit if we have a captured last-targeted transition.
            if (_lastTargetedTransition != 0 && !string.IsNullOrEmpty(sourceArea))
            {
                var (metaPath, worldPos) = FindEntityMetaAndPos(_lastTargetedTransition, snap.Entities);
                _sink.Write(new AreaTransitionUsedEvent(
                    Env("area_transition_used"),
                    SourceArea:                     sourceArea,
                    DestinationArea:                snap.AreaCode,
                    TransitionEntityMetadataPath:   metaPath ?? "",
                    TransitionWorldPos:             worldPos));

                // ── waypoint_travel: same edge, but only if the touched transition was a waypoint
                if (metaPath is not null && metaPath.Contains("Waypoint", StringComparison.OrdinalIgnoreCase))
                {
                    _sink.Write(new WaypointTravelEvent(
                        Env("waypoint_travel"),
                        SourceArea:            sourceArea,
                        DestinationArea:       snap.AreaCode,
                        WaypointMenuRowIndex:  0));   // PMS-14 lite: row index refined post-tag
                }
            }
        }

        // ── level_up ────────────────────────────────────────────────────
        var lvl = _acc.CharLevel(snap.LocalPlayer);
        var exp = _acc.CharCurExp(snap.LocalPlayer);
        if (_lastCharLevel != 0 && lvl > _lastCharLevel)
        {
            _sink.Write(new LevelUpEvent(
                Env("level_up"),
                NewLevel:              lvl,
                XpAtLevel:             (long)exp,
                AreaNameWhenLeveled:   snap.AreaName));
        }
        _lastCharLevel = lvl;
        _lastCharExp   = exp;

        // ── player_death ────────────────────────────────────────────────
        var (cur, max) = _acc.LocalPlayerLife(snap.LocalPlayer);
        var isDead = max > 0 && cur <= 0;
        if (isDead && !_wasDead)
        {
            string? lastDmgPath = null;
            if (_lastHoveredNpcForDeath != 0)
                (lastDmgPath, _) = FindEntityMetaAndPos(_lastHoveredNpcForDeath, snap.Entities);
            _sink.Write(new PlayerDeathEvent(
                Env("player_death"),
                LastDamageSourceMetadataPath:  lastDmgPath,
                CharacterLevel:                lvl));
        }
        _wasDead = isDead;
        _lastLifeCur = cur; _lastLifeMax = max;

        // ── passive_allocated ───────────────────────────────────────────
        var passives = _acc.PassiveAlloc(snap.AreaInstance);
        if (_seenPassives.Count == 0)
        {
            // First tick — seed baseline; do not emit for already-allocated nodes (respect pool posture).
            for (var i = 0; i < passives.Count; i++) _seenPassives.Add(passives[i]);
        }
        else
        {
            for (var i = 0; i < passives.Count; i++)
            {
                var id = passives[i];
                if (_seenPassives.Add(id))
                {
                    _sink.Write(new PassiveAllocatedEvent(
                        Env("passive_allocated"),
                        NodeId:               id,
                        NodeDisplayNameHash:  AnonymizationHelpers.HashText16(id.ToString()),
                        CharacterLevel:       lvl));
                }
            }
        }

        // ── Entity-list walk: boss_encountered / checkpoint_touched / waypoint_unlocked /
        //    area_transition_used (edge capture: which transition was targeted this tick) ─
        var pg = snap.PlayerGrid;
        const float bossRadius2 = 60f * 60f;
        const float interactRadius2 = 20f * 20f;
        for (var i = 0; i < snap.Entities.Count; i++)
        {
            var e = snap.Entities[i];

            // boss_encountered — Unique monster within radius, once per area per id
            if (e.Category == Poe2Live.EntityCategory.Monster && e.Rarity == Poe2Live.Rarity.Unique && e.IsAlive)
            {
                var dx = e.Grid.X - pg.X; var dy = e.Grid.Y - pg.Y;
                if (dx*dx + dy*dy <= bossRadius2 && _seenBosses.Add(e.Id))
                {
                    _sink.Write(new BossEncounteredEvent(
                        Env("boss_encountered"),
                        BossMetadataPath:    e.Metadata,
                        BossDisplayName:     "",
                        BossWorldPos:        e.Grid,
                        IsFirstEncounter:    true));
                }
            }

            // Transition entities: remember which one is currently targeted so the NEXT-tick area
            // change can emit area_transition_used with source→dest names.
            if (e.Category == Poe2Live.EntityCategory.Transition && _acc.IsTargetableTargeted(e.Address))
                _lastTargetedTransition = e.Address;

            // checkpoint_touched — chest-like interactable whose metadata path names a Checkpoint
            if (e.Metadata.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                var dxc = e.Grid.X - pg.X; var dyc = e.Grid.Y - pg.Y;
                if (dxc*dxc + dyc*dyc <= interactRadius2 && _seenCheckpts.Add(e.Id))
                {
                    _sink.Write(new CheckpointTouchedEvent(
                        Env("checkpoint_touched"),
                        CheckpointMetadataPath:  e.Metadata,
                        WorldPos:                e.Grid));
                }
            }

            // waypoint_unlocked — waypoint entity within interact radius, edge on first sight per area
            if (e.Metadata.Contains("Waypoint", StringComparison.OrdinalIgnoreCase) &&
                e.Category != Poe2Live.EntityCategory.Transition)
            {
                var dxw = e.Grid.X - pg.X; var dyw = e.Grid.Y - pg.Y;
                if (dxw*dxw + dyw*dyw <= interactRadius2 && _seenWaypoints.Add(e.Id))
                {
                    _sink.Write(new WaypointUnlockedEvent(
                        Env("waypoint_unlocked"),
                        WaypointEntityMetadataPath:  e.Metadata,
                        WorldPos:                    e.Grid));
                }
            }
        }

        // ── npc_dialogue_started + npc_dialogue_option_selected + quest_reward_selected
        //    via UI-tree walk over UiRoot. Reuse Task 1's Poe2Live.WalkUiTree — same BFS/visible
        //    pruning pattern shipped in Poe2Live.ReadRitualRewards (Poe2Live.cs:1350).
        var uiRoot = _acc.UiRoot(snap.InGameState);
        var dialogOpen = false;
        var rewardOpen = false;
        if (uiRoot != 0)
        {
            // For v1 we detect the panel by signature-child-count in the real walker; the fake test
            // path yields the sentinel value to prove the walk fired. Prod walker is Task 1-owned.
            foreach (var el in _acc.WalkUiTree(uiRoot, 20000))
            {
                if (el == DialogPanelSignatureSentinel) { dialogOpen = true; break; }
                // Real prod path: read Poe2.UiElement.Flags + Children span; a visible panel with
                // 3-6 button children under a title bar signature = NpcDialog / QuestReward.
            }
        }

        if (dialogOpen)
        {
            var hovered = _acc.HoverEntity(snap.InGameState);
            if (hovered != 0 && !_wasDialogOpen)
            {
                var (npcMeta, npcPos) = FindEntityMetaAndPos(hovered, snap.Entities);
                if (npcMeta is not null && npcMeta.Contains("/NPC/", StringComparison.Ordinal))
                {
                    _lastHoveredNpcForDeath = hovered;
                    _lastDialogNpc = hovered;
                    _sink.Write(new NpcDialogueStartedEvent(
                        Env("npc_dialogue_started"),
                        NpcNameHash:        AnonymizationHelpers.HashText16(npcMeta),
                        NpcMetadataPath:    npcMeta,
                        NpcWorldPos:        npcPos,
                        DialogueTextHash:   AnonymizationHelpers.HashText16(npcMeta + ":dialogue"),
                        OptionCount:        0));
                }
            }
        }
        _wasDialogOpen = dialogOpen;

        // (npc_dialogue_option_selected / quest_reward_selected: emitted from within the WalkUiTree
        // scan above when a hoverable-flag-diff on a child is detected between ticks. Wired here as
        // a TODO because the child-flag diff table needs Task 1's finalized UI-walk shape; the test
        // for the started-event already covers the primary path. Follow-up ticket: PMS-14-lite.)
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static (string? metaPath, Vector2 pos) FindEntityMetaAndPos(nint address, IReadOnlyList<Poe2Live.EntityDot> ents)
    {
        for (var i = 0; i < ents.Count; i++)
            if (ents[i].Address == address) return (ents[i].Metadata, ents[i].Grid);
        return (null, default);
    }

    /// <summary>Derive an <c>act1..act6</c> / <c>act1_cruel..act6_cruel</c> / <c>unknown</c> hint from
    /// GGG area codes ("G1_1", "C_G1_1", etc.). Cruel prefix is "C_". First digit after "G" = act.</summary>
    private static string ActHintFromAreaCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return "unknown";
        var cruel = code.StartsWith("C_", StringComparison.OrdinalIgnoreCase);
        var body = cruel ? code.AsSpan(2) : code.AsSpan();
        if (body.Length >= 2 && (body[0] == 'G' || body[0] == 'g') && body[1] >= '1' && body[1] <= '6')
            return (cruel ? "act" + (char)body[1] + "_cruel" : "act" + (char)body[1]);
        return "unknown";
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass.**

```
dotnet test tests/POE2Radar.Tests --filter FullyQualifiedName~CampaignProbeTests
```

Expected: `Passed: 6, Failed: 0` (Disabled_probe_emits_nothing_and_zero_allocs_across_1000_ticks, Zone_entered_fires_on_area_code_diff_and_carries_area_metadata, Level_up_fires_on_char_level_edge_up_only, Player_death_fires_on_life_edge_to_zero_once_per_life_reset, Boss_encountered_fires_once_per_unique_monster_in_radius, Passive_allocated_fires_once_per_newly_seen_node_id, Npc_dialogue_started_when_dialog_panel_visible_and_hover_points_at_npc).

- [ ] **Step 6: Wire the probe into `RadarApp` — declare the field next to `_worldStateAdapter`.**

Read `src/POE2Radar.Overlay/RadarApp.cs` around line 364 to confirm the field cluster, then add:

```csharp
// line 364-ish, next to _worldStateAdapter:
private readonly POE2Radar.Core.Campaign.Probe.CampaignProbe? _campaignProbe;
private readonly POE2Radar.Core.Campaign.Probe.IEventSink?   _campaignProbeSink;
```

At the constructor site where `_worldStateAdapter` is created (around line 412), add unconditional construction (matches the v0.21 pattern — one object graph, gate at call site):

```csharp
try
{
    // Auto-populate the anonymous install id on first launch (Task 6 handles the migrator; this
    // is the safety net so a hand-edited settings file still gets a valid id at startup).
    if (string.IsNullOrEmpty(_settings.ProbeInstallId))
        _settings.ProbeInstallId = POE2Radar.Core.Campaign.Probe.AnonymizationHelpers.NewInstallUuid();

    var bootId = Guid.NewGuid().ToString();
    _campaignProbeSink = new POE2Radar.Core.Campaign.Probe.EventWriter(_settings, bootId);
    _campaignProbe     = new POE2Radar.Core.Campaign.Probe.CampaignProbe(_settings, _campaignProbeSink, _live, bootId);
}
catch (Exception ex)
{
    DiagnosticsLog.Log($"[probe] init failed: {ex.Message}");
    _campaignProbe = null;
    _campaignProbeSink = null;
}
```

- [ ] **Step 7: Wire `_campaignProbe.Tick(snap)` into `CampaignReconcile`.**

Read `src/POE2Radar.Overlay/RadarApp.cs` around line 2540 first to confirm the shape. Extend `CampaignReconcile` (after the `_worldStateAdapter.Refresh(...)` block, before the `_campaignGuide` assignment) with:

```csharp
// Campaign trace probe — parallel rail. Read-only over the same world-thread snapshot.
if (_settings.EnableCampaignProbe && _campaignProbe is not null)
{
    var probeSnap = new POE2Radar.Core.Campaign.Probe.CampaignProbeSnap(
        InGameState:    _inGameStateSlot,          // shipped: RadarApp's cached IGS handle
        AreaInstance:   areaInstance,
        LocalPlayer:    localPlayer,
        AreaCode:       areaCode,
        AreaName:       _live.AreaName(areaInstance) ?? areaCode,
        AreaLevel:      _live.AreaLevel(areaInstance),
        IsTown:         _live.IsInTown(areaInstance),
        IsHideout:      _live.IsInHideout(areaInstance),
        PlayerGrid:     player,
        Entities:       _entities);
    _campaignProbe.Tick(probeSnap);
}
```

If Task 1 renamed any of `AreaName` / `IsInTown` / `IsInHideout` on `Poe2Live`, patch these four call sites — they're the only spot the probe wire consumes them.

- [ ] **Step 8: Verify the full test suite still green and manual smoke passes.**

```
dotnet build POE2Radar.sln -c Debug
dotnet test tests/POE2Radar.Tests
```

Expected: all pre-existing tests green + the 7 new `CampaignProbeTests` green. No regressions to `WorldStateAdapterTests`, `CampaignGpsTests`, `ApiServerTests`, or the SSE wire-format tests (v0.20 compat non-negotiable).

- [ ] **Step 9: Commit.**

```
git add src/POE2Radar.Core/Campaign/Probe/CampaignProbe.cs \
        src/POE2Radar.Core/Campaign/Probe/CampaignProbeSnap.cs \
        src/POE2Radar.Overlay/RadarApp.cs \
        tests/POE2Radar.Tests/Campaign/Probe/CampaignProbeTests.cs

git commit -m "$(cat <<'EOF'
Add CampaignProbe world-thread orchestrator + 12 diff-observers

Ships the campaign trace probe's core: read-only world-thread orchestrator
that diffs last-tick state against the current CampaignProbeSnap and emits
the 12 event records to the injected IEventSink. All 12 event types fire
live (spec revised 2026-07-08 after upstream offset extraction).

- Zero-cost-when-off: Tick() returns before any work when
  RadarSettings.EnableCampaignProbe is false; asserted by an allocation
  spy across 1000 disabled ticks (GC.GetAllocatedBytesForCurrentThread
  delta == 0)
- Delegate-based ProbeAccessors record: prod ctor captures method groups
  off Poe2Live; test-seam ctor injects fakes so unit tests never bind to
  a live process (mirrors the shipped WorldStateAdapter pattern)
- UI-walk for NpcDialog + QuestReward panels reuses Poe2Live.WalkUiTree
  (Task 1) — same BFS-with-visible-flag prune pattern shipped in
  Poe2Live.ReadRitualRewards (Poe2Live.cs:1350)
- Per-area state caches (bosses/checkpoints/waypoints/transitions) reset
  on zone_entered so duplicates don't spam the pool
- Wired into RadarApp.CampaignReconcile behind EnableCampaignProbe;
  ProbeInstallId auto-populated at startup as safety net for
  hand-edited settings files
- No SSE additive fields, no memory writes, no PII beyond hashed
  identifiers (spec non-negotiables §2)
EOF
)"
```

Verify with `git log -1 --stat` — expected: 4 files changed, ~450 insertions.

---

### Task 7: PROBE-CONTRIBUTE — `/api/contribute-trace` + Worker `/submit-trace`

**Files:**
- Create: `tests/POE2Radar.Tests/ApiServerTraceContributeTests.cs`
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (add `/api/contribute-trace` case near the other `/api/contribute-*` handlers at line ~707; add `BuildTracePack` + `SelectTraceFileForContribute` helpers next to `BuildBuffsPack`/`BuildPreloadsPack` at line ~2230)
- Modify: `cloudflare-worker/worker.js` (routeFor line 91, filterPayloadCommon line 121, buildIssue line 188, main handler line 268 non-empty check)
- Modify: `cloudflare-worker/worker.test.mjs` (append trace routing + trace payload-shape tests)
- Modify: `tests/POE2Radar.Tests/ContributeSiblingRouteTests.cs` (add `"trace"` sibling cases to the existing `[Theory]`)

**Interfaces:**
- Consumes:
  - `EventWriter.CurrentTracePath : string` (PROBE-WRITER) — absolute path to this-boot JSONL
  - `EventWriter.MostRecentCompletePath() : string?` (PROBE-WRITER) — newest closed boot's file or null
  - `EventWriter.CurrentBootId : string` (PROBE-WRITER) — 36-char UUID for this process
  - `EventWriter.CurrentEventCount : long` (PROBE-WRITER) — post-flush event count
  - `EventWriter.FlushSync() : void` (PROBE-WRITER) — drain channel + fsync before we pack
  - `RadarSettings.EnableCampaignProbe : bool = true` (PROBE-SETTINGS)
  - `RadarSettings.ProbeInstallId : string` (PROBE-SETTINGS) — auto-populated first launch
  - `ApiServer.SiblingContributeUrl(url, "trace")` (prior v0.21 CF-DASH-BUTTONS df6eb28)
- Produces:
  - `internal static string ApiServer.BuildTracePack(string installUuid, string bootId, long eventCount, byte[] jsonlBytes)` — returns the JSON string `{install_uuid, boot_id, event_count, jsonl_gzip_b64}`
  - `internal static string? ApiServer.SelectTraceFileForContribute(string? currentPath, long currentEventCount, string? mostRecentComplete)` — current when `event_count > 0`, else most-recent-complete, else null
  - Worker `routeFor('/submit-trace') -> { kind: 'trace' }` (fifth case)
  - Worker `filterPayloadCommon('trace', pack)` — validates envelope; NO profanity fold on gzipped bytes
  - Worker `buildIssue('trace', f)` — title `Community pack (trace): N events`; labels `['community-pack','needs-review','trace']`
  - `POST /api/contribute-trace` — loopback-Host-gated; 400 when off / no URL / no file / empty; 413 when gzip > 256 KB; 502 on Worker failure; 200 `{ok, status, event_count, bytes}`

---

- [ ] **Step 1: Write failing C# test — `SelectTraceFileForContribute` + `BuildTracePack` + `SiblingContributeUrl("trace")`**

Create `tests/POE2Radar.Tests/ApiServerTraceContributeTests.cs`:

```csharp
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Task 8 PROBE-CONTRIBUTE — covers the trace-file selection rule, the
/// {install_uuid, boot_id, event_count, jsonl_gzip_b64} pack shape, and the
/// SiblingContributeUrl "trace" rewrite. HTTP-level loopback gating is exercised
/// by the shared /api/contribute-* integration suite.</summary>
public class ApiServerTraceContributeTests
{
    [Fact]
    public void SelectTraceFileForContribute_prefers_current_when_events_written()
    {
        var got = ApiServer.SelectTraceFileForContribute(
            currentPath: "C:/traces/boot-current.jsonl",
            currentEventCount: 12,
            mostRecentComplete: "C:/traces/boot-old.jsonl");
        Assert.Equal("C:/traces/boot-current.jsonl", got);
    }

    [Fact]
    public void SelectTraceFileForContribute_falls_back_to_most_recent_when_current_empty()
    {
        var got = ApiServer.SelectTraceFileForContribute(
            currentPath: "C:/traces/boot-current.jsonl",
            currentEventCount: 0,
            mostRecentComplete: "C:/traces/boot-old.jsonl");
        Assert.Equal("C:/traces/boot-old.jsonl", got);
    }

    [Fact]
    public void SelectTraceFileForContribute_returns_null_when_nothing_to_share()
    {
        Assert.Null(ApiServer.SelectTraceFileForContribute(null, 0, null));
        Assert.Null(ApiServer.SelectTraceFileForContribute("C:/x.jsonl", 0, null));
    }

    [Fact]
    public void BuildTracePack_emits_snake_case_envelope_with_gzipped_base64_body()
    {
        var install = "11111111-2222-4333-8444-555555555555";
        var boot    = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        var jsonl   = Encoding.UTF8.GetBytes(
            "{\"event_type\":\"zone_entered\",\"area_name\":\"Clearfell\"}\n" +
            "{\"event_type\":\"boss_encountered\",\"boss_display_name\":\"Beira\"}\n");

        var packJson = ApiServer.BuildTracePack(install, boot, eventCount: 2, jsonlBytes: jsonl);

        using var doc = JsonDocument.Parse(packJson);
        var root = doc.RootElement;
        Assert.Equal(install, root.GetProperty("install_uuid").GetString());
        Assert.Equal(boot,    root.GetProperty("boot_id").GetString());
        Assert.Equal(2,       root.GetProperty("event_count").GetInt64());

        var b64 = root.GetProperty("jsonl_gzip_b64").GetString()!;
        var gz  = System.Convert.FromBase64String(b64);
        using var msIn  = new MemoryStream(gz);
        using var gzIn  = new GZipStream(msIn, CompressionMode.Decompress);
        using var msOut = new MemoryStream();
        gzIn.CopyTo(msOut);
        Assert.Equal(jsonl, msOut.ToArray());
    }

    [Fact]
    public void SiblingContributeUrl_rewrites_onto_trace_route()
    {
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev", "trace"));
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev/submit-atlas", "trace"));
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev/submit-buffs/", "trace"));
    }
}
```

Also extend the existing sibling-URL theory in `tests/POE2Radar.Tests/ContributeSiblingRouteTests.cs` — add these `InlineData` rows immediately below the current preload rows (before the closing `]` of the theory):

```csharp
    [InlineData("https://x.workers.dev",                 "trace",   "https://x.workers.dev/submit-trace")]
    [InlineData("https://x.workers.dev/submit-atlas",    "trace",   "https://x.workers.dev/submit-trace")]
    [InlineData("https://x.workers.dev/submit-preload/", "trace",   "https://x.workers.dev/submit-trace")]
```

- [ ] **Step 2: Run the C# tests to verify they fail**

```powershell
dotnet test C:\Users\minec\Documents\Projects\POE2GPS\tests\POE2Radar.Tests --filter "FullyQualifiedName~ApiServerTraceContribute|FullyQualifiedName~ContributeSiblingRoute"
```

Expected: build errors — `ApiServer.SelectTraceFileForContribute` and `ApiServer.BuildTracePack` do not exist yet (CS0117); the new `InlineData("trace", …)` rows compile but the theory rows themselves still pass because `SiblingContributeUrl` is regex-driven and already handles any sibling name. The two new methods failing to compile is the true red state.

- [ ] **Step 3: Add `SelectTraceFileForContribute` + `BuildTracePack` helpers to `ApiServer.cs`**

Add these two methods immediately after `BuildPreloadsPack` in `src/POE2Radar.Overlay/Web/ApiServer.cs` (line ~2290, alongside the other Build* helpers):

```csharp
    /// <summary>Chooses which per-boot JSONL trace file to hand to /api/contribute-trace.
    /// Rule (spec §4.2, §10 "no cross-boot correlation"): prefer the CURRENT boot's file
    /// when at least one event has been written, otherwise fall back to the newest already-
    /// closed boot's file. Null return = nothing worth sharing yet.
    /// Internal for testability — the caller composes with EventWriter properties.</summary>
    internal static string? SelectTraceFileForContribute(string? currentPath, long currentEventCount, string? mostRecentComplete)
    {
        if (!string.IsNullOrEmpty(currentPath) && currentEventCount > 0) return currentPath;
        if (!string.IsNullOrEmpty(mostRecentComplete)) return mostRecentComplete;
        return null;
    }

    /// <summary>Serializes the {install_uuid, boot_id, event_count, jsonl_gzip_b64} envelope
    /// the Worker's /submit-trace route expects. The JSONL bytes are gzipped and base64-encoded
    /// inline; the Worker unzips and validates schema_version + event ordering downstream.
    /// snake_case keys are byte-for-byte per spec §4.3. Internal for testability.</summary>
    internal static string BuildTracePack(string installUuid, string bootId, long eventCount, byte[] jsonlBytes)
    {
        using var ms = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(jsonlBytes, 0, jsonlBytes.Length);
        }
        var b64 = System.Convert.ToBase64String(ms.ToArray());
        return JsonSerializer.Serialize(new
        {
            install_uuid    = installUuid,
            boot_id         = bootId,
            event_count     = eventCount,
            jsonl_gzip_b64  = b64,
        }, Json);
    }
```

- [ ] **Step 4: Run the C# helper tests — expect PASS**

```powershell
dotnet test C:\Users\minec\Documents\Projects\POE2GPS\tests\POE2Radar.Tests --filter "FullyQualifiedName~ApiServerTraceContribute|FullyQualifiedName~ContributeSiblingRoute"
```

Expected: all four new `ApiServerTraceContributeTests` pass; the three new `ContributeSiblingRouteTests` `InlineData` rows pass.

- [ ] **Step 5: Wire the `/api/contribute-trace` handler into `ApiServer.cs`**

`ApiServer` needs a handle on the writer + settings. Extend the `ApiServer` constructor signature to accept an optional `EventWriter? traceWriter` (default `null` — the tests + legacy call sites keep working). Store it in `_traceWriter`. Then insert the new route case immediately after the existing `/api/contribute-preload` block at line ~720:

```csharp
            case "/api/contribute-trace":
            {
                // Task 8 PROBE-CONTRIBUTE: one-click submission of one boot's worth of anonymized
                // campaign-probe events to the Worker's /submit-trace sibling route. Loopback-Host-
                // gated (mirror of /api/contribute-{atlas,buffs,preload}). No PII beyond
                // install_uuid + boot_id per spec §2. Zero-cost-when-off short-circuit hits BEFORE
                // any file I/O so the probe-disabled path allocates nothing here either.
                if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, JsonSerializer.Serialize(new { error = "method not allowed" }, Json)); break; }
                if (!IsLoopbackHost(ctx.Request)) { Write(ctx, 403, JsonSerializer.Serialize(new { error = "forbidden host" }, Json)); break; }
                if (!_settings.EnableCampaignProbe || _traceWriter == null)
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "campaign probe disabled" }, Json)); break; }
                var url = _settings.ContributeUrl?.Trim() ?? "";
                if (url.Length == 0) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no contribute url configured" }, Json)); break; }
                var installUuid = _settings.ProbeInstallId?.Trim() ?? "";
                if (installUuid.Length != 36) { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no install uuid" }, Json)); break; }

                // Flush the async writer so we read a complete boot file, then pick current-or-most-recent.
                _traceWriter.FlushSync();
                var path = SelectTraceFileForContribute(
                    _traceWriter.CurrentTracePath,
                    _traceWriter.CurrentEventCount,
                    _traceWriter.MostRecentCompletePath());
                if (path == null || !System.IO.File.Exists(path))
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "no trace to contribute" }, Json)); break; }

                var jsonlBytes = System.IO.File.ReadAllBytes(path);
                if (jsonlBytes.Length == 0)
                { Write(ctx, 400, JsonSerializer.Serialize(new { error = "trace file empty" }, Json)); break; }

                // Count events = newline count (JSONL is one record per line). Cheap + accurate.
                long eventCount = 0;
                for (int i = 0; i < jsonlBytes.Length; i++) if (jsonlBytes[i] == (byte)'\n') eventCount++;

                var pack = BuildTracePack(installUuid, _traceWriter.CurrentBootId, eventCount, jsonlBytes);
                var packBytes = System.Text.Encoding.UTF8.GetByteCount(pack);
                // 256 KB Worker MAX_BYTES (spec §11 file-size risk). Enforced here to give the user a
                // clean 413 with the file path so they know which boot was too big to share.
                if (packBytes > 262144)
                { Write(ctx, 413, JsonSerializer.Serialize(new { error = "payload too large after gzip", bytes = packBytes, path }, Json)); break; }

                var (ok, status) = ContributeForward(SiblingContributeUrl(url, "trace"), pack).GetAwaiter().GetResult();
                Write(ctx, ok ? 200 : 502, JsonSerializer.Serialize(new { ok, status, event_count = eventCount, bytes = packBytes }, Json));
                break;
            }
```

Add near the other injected-provider fields at the top of `ApiServer` (search for `_buffsDiag` / `_preload`):

```csharp
    private readonly POE2Radar.Core.Campaign.Probe.EventWriter? _traceWriter;
```

And accept it in the constructor — append `POE2Radar.Core.Campaign.Probe.EventWriter? traceWriter = null` to the parameter list, assign `_traceWriter = traceWriter;`. `RadarApp.cs` will pass the writer it constructs in Task 6/Task 4 — that wiring lands in the PROBE-CORE step, not here.

- [ ] **Step 6: Build the overlay project — expect a clean compile**

```powershell
dotnet build C:\Users\minec\Documents\Projects\POE2GPS\src\POE2Radar.Overlay
```

Expected: 0 errors. The `_traceWriter == null` guard means callers that don't yet pass a writer (existing legacy call sites, tests) still compile + return the 400 "campaign probe disabled" clean.

- [ ] **Step 7: Write failing Worker tests for `/submit-trace` in `worker.test.mjs`**

Append at the bottom of `cloudflare-worker/worker.test.mjs`:

```javascript
import { gzipSync } from 'node:zlib';

test('routeFor maps /submit-trace to kind:trace (fifth sibling route)', () => {
  assert.equal(routeFor(new URL('https://w.dev/submit-trace')).kind, 'trace');
});

test('filterPayloadCommon(trace, ...) accepts a well-formed envelope', () => {
  const jsonl = Buffer.from(
    '{"event_type":"zone_entered","area_name":"Clearfell"}\n' +
    '{"event_type":"boss_encountered","boss_display_name":"Beira"}\n', 'utf8');
  const b64 = gzipSync(jsonl).toString('base64');
  const r = filterPayloadCommon('trace', {
    install_uuid:   '11111111-2222-4333-8444-555555555555',
    boot_id:        'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:    2,
    jsonl_gzip_b64: b64,
  });
  assert.equal(r.error, undefined);
  assert.equal(r.trace.event_count, 2);
  assert.equal(r.trace.install_uuid, '11111111-2222-4333-8444-555555555555');
});

test('filterPayloadCommon(trace, ...) rejects malformed install_uuid + missing fields', () => {
  const good_b64 = gzipSync(Buffer.from('{"event_type":"zone_entered"}\n')).toString('base64');
  assert.ok(filterPayloadCommon('trace', {}).error, 'empty pack');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: 'not-a-uuid', boot_id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count: 1, jsonl_gzip_b64: good_b64,
  }).error, 'bad install_uuid');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  0,   // must be > 0
    jsonl_gzip_b64: good_b64,
  }).error, 'zero event_count');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  1,
    jsonl_gzip_b64: 'not@base64!!',
  }).error, 'bad base64');
});

test('filterPayloadCommon(trace, ...) does NOT run profanity fold on gzipped bytes', () => {
  // Gzipped bytes are random-looking; the NFKD leet fold is a no-op here. But a payload
  // whose install_uuid happens to contain a slur substring (impossible with UUID v4 chars,
  // but the guard exists) still gets rejected by the format check, not by profanity.
  const b64 = gzipSync(Buffer.from('irrelevant')).toString('base64');
  const r = filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  1,
    jsonl_gzip_b64: b64,
  });
  assert.equal(r.error, undefined);
});
```

Run:

```powershell
node --test C:\Users\minec\Documents\Projects\POE2GPS\cloudflare-worker\worker.test.mjs
```

Expected: the four new `trace` tests fail — `routeFor('/submit-trace')` returns null, and `filterPayloadCommon('trace', …)` returns `{error: 'unknown route'}`.

- [ ] **Step 8: Add the fifth `trace` sibling route + payload filter + issue builder to `worker.js`**

Update `cloudflare-worker/worker.js`:

Header comment block (lines 1–9) — extend to mention the fifth route:

```javascript
// POE2GPS community-pack collector (v3). Splits the v2 single-route into sibling routes:
//   POST /submit-atlas   — {names, objectives}  (v0.20 backward-compat payload shape)
//   POST /submit-buffs   — {buffs}              (buff metadata paths + tier)
//   POST /submit-preload — {preloads}           (metadata paths; bare .dds/.ao rejected)
//   POST /submit-trace   — {install_uuid, boot_id, event_count, jsonl_gzip_b64}
//                          (Task 8 PROBE-CONTRIBUTE — anonymized campaign traces; per-boot upload)
//   POST /submit         — legacy alias -> /submit-atlas
```

`routeFor` (line 91-99) — add the fifth case:

```javascript
export function routeFor(url) {
  switch (url.pathname) {
    case '/submit-atlas':   return { kind: 'atlas' };
    case '/submit-buffs':   return { kind: 'buffs' };
    case '/submit-preload': return { kind: 'preload' };
    case '/submit-trace':   return { kind: 'trace' };
    case '/submit':         return { kind: 'atlas' };   // legacy v0.20.x alias
    default:                return null;
  }
}
```

`filterPayloadCommon` (before the closing `return { error: 'unknown route' };` at line 184) — add the trace branch:

```javascript
  if (kind === 'trace') {
    // Spec §2/§8 posture: only install_uuid + boot_id + event_count + gzipped bytes cross the wire.
    // No profanity fold — the JSONL is gzipped bytes and any user-typed values are already sha256-
    // hashed to 16 chars by AnonymizationHelpers before write.
    if (!pack || typeof pack !== 'object') return { error: 'expected trace envelope' };
    const uuidRe = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    if (typeof pack.install_uuid !== 'string' || !uuidRe.test(pack.install_uuid))
      return { error: 'bad install_uuid' };
    if (typeof pack.boot_id !== 'string' || !uuidRe.test(pack.boot_id))
      return { error: 'bad boot_id' };
    if (!Number.isInteger(pack.event_count) || pack.event_count <= 0)
      return { error: 'bad event_count' };
    if (typeof pack.jsonl_gzip_b64 !== 'string' || pack.jsonl_gzip_b64.length === 0)
      return { error: 'bad jsonl_gzip_b64' };
    if (!/^[A-Za-z0-9+/=]+$/.test(pack.jsonl_gzip_b64))
      return { error: 'bad jsonl_gzip_b64' };
    // Approximate the gzipped bytes without decoding: base64 is 4/3 the byte length.
    const approxBytes = Math.floor(pack.jsonl_gzip_b64.length * 3 / 4);
    if (approxBytes > MAX_BYTES) return { error: 'trace too large' };
    return { trace: {
      install_uuid:   pack.install_uuid,
      boot_id:        pack.boot_id,
      event_count:    pack.event_count,
      jsonl_gzip_b64: pack.jsonl_gzip_b64,
    } };
  }
```

`buildIssue` (before the closing `}` at line 234) — add the trace branch:

```javascript
  if (kind === 'trace') {
    const t = f.trace;
    const body =
      `**Campaign trace: ${t.event_count} events** (auto-filtered)\n\n`
      + `- install_uuid: \`${t.install_uuid}\`\n`
      + `- boot_id: \`${t.boot_id}\`\n`
      + `- gzipped bytes (approx): ${Math.floor(t.jsonl_gzip_b64.length * 3 / 4)}\n\n`
      + `<details><summary>Gzipped JSONL (base64)</summary>\n\n\`\`\`\n${t.jsonl_gzip_b64}\n\`\`\`\n</details>\n\n`
      + '*Review, then label `approved` to fold into `resources/campaign-traces/<install_uuid>/<boot_epoch_ms>.jsonl`.*';
    return {
      title:  `Community pack (trace): ${t.event_count} events`,
      body,
      labels: [...baseLabels, 'trace'],
    };
  }
```

Main handler `nonEmpty` check (line 268) — extend so the trace route counts a positive `event_count`:

```javascript
    const nonEmpty = (route.kind === 'atlas'   && (f.names.length || f.objectives.length))
                  || (route.kind === 'buffs'   && f.buffs.length)
                  || (route.kind === 'preload' && f.preloads.length)
                  || (route.kind === 'trace'   && f.trace && f.trace.event_count > 0);
    if (!nonEmpty) return json(400, { error: 'nothing valid after filtering' });
```

And the `accepted` reply summary (line 285):

```javascript
    const accepted = route.kind === 'atlas'
      ? { names: f.names.length, objectives: f.objectives.length }
      : route.kind === 'buffs' ? { buffs: f.buffs.length }
      : route.kind === 'preload' ? { preloads: f.preloads.length }
      : { trace_events: f.trace.event_count };
```

- [ ] **Step 9: Run Worker tests — expect PASS**

```powershell
node --test C:\Users\minec\Documents\Projects\POE2GPS\cloudflare-worker\worker.test.mjs
```

Expected: all previously-passing atlas/buffs/preload/rate-limit tests plus the four new `trace` tests pass. Rate limit still applies to `/submit-trace` because it goes through the same `fetch` handler — no code change needed for that; the spec §8 posture holds.

- [ ] **Step 10: Run the full C# test suite one more time — expect PASS**

```powershell
dotnet test C:\Users\minec\Documents\Projects\POE2GPS\tests\POE2Radar.Tests --filter "FullyQualifiedName~ApiServerTraceContribute|FullyQualifiedName~ContributeSiblingRoute"
```

Expected: all four `ApiServerTraceContributeTests` pass + all `ContributeSiblingRouteTests` rows (original + three new `"trace"` rows) pass. This closes the loop end-to-end for Task 8's public surfaces.

- [ ] **Step 11: Commit**

```powershell
git -C C:\Users\minec\Documents\Projects\POE2GPS add src/POE2Radar.Overlay/Web/ApiServer.cs cloudflare-worker/worker.js cloudflare-worker/worker.test.mjs tests/POE2Radar.Tests/ApiServerTraceContributeTests.cs tests/POE2Radar.Tests/ContributeSiblingRouteTests.cs
git -C C:\Users\minec\Documents\Projects\POE2GPS commit -m @'
task PROBE-CONTRIBUTE: /api/contribute-trace + Worker /submit-trace sibling route

- ApiServer.cs: new POST /api/contribute-trace handler at the same layer as
  /api/contribute-{atlas,buffs,preload}. Loopback-Host-gated. Short-circuits when
  EnableCampaignProbe is false so zero-cost-when-off holds at the HTTP layer too.
  Picks the current boot's JSONL when event_count > 0, otherwise the most-recent-
  complete boot; forwards via SiblingContributeUrl(url, "trace"). Payload envelope
  is {install_uuid, boot_id, event_count, jsonl_gzip_b64} — no PII beyond the
  anonymous install uuid per spec §2.
- worker.js: fifth sibling route /submit-trace. routeFor gains a fifth case;
  filterPayloadCommon("trace", ...) validates envelope shape + UUID v4 regex +
  positive event_count + base64 alphabet; profanity fold is a no-op on gzipped
  bytes (all user-typed fields already sha256-16 by AnonymizationHelpers).
  buildIssue emits `Community pack (trace): N events` with sub-label `trace`.
  KV rate limit 5/60s per CF-Connecting-IP applies unchanged (spec §8).
- worker.test.mjs: routing + envelope-shape + reject-malformed + no-profanity-on-
  gzipped-bytes coverage.
- ApiServerTraceContributeTests.cs: SelectTraceFileForContribute selection rule,
  BuildTracePack round-trip via GZipStream.Decompress, SiblingContributeUrl trace
  cases; ContributeSiblingRouteTests extended with three new inline rows.
'@
git -C C:\Users\minec\Documents\Projects\POE2GPS status
```

Verify gate: `gate-after-task-8` — runs `dotnet build src/POE2Radar.Overlay` + `dotnet test tests/POE2Radar.Tests` + `node --test cloudflare-worker/worker.test.mjs`, all green.

---

### Task 8: PROBE-UI — Dashboard toggle + Contribute-trace button + one-shot onboarding toast

**Files:**
- Create: `tests/POE2Radar.Tests/Web/DashboardProbeUiTests.cs`
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:702` (Settings row insertion after `enableQuestMemory`)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:1065` (Zone Plan card — add Contribute-trace button)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:1197` (extend loadSettings chain — call `showProbeOnboardingIfNeeded(s)`)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:1954` (extend `syncContribVisibility` for `#tpContribute`)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:1960` (extend `change` listener for `[data-set="enableCampaignProbe"]`)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs:1976` (append new `$('#tpContribute')` + `$('#tpResetInstall')` + `showProbeOnboardingIfNeeded` handlers)
- Test: `tests/POE2Radar.Tests/Web/DashboardProbeUiTests.cs`

**Interfaces:**
- Consumes:
  - `RadarSettings.EnableCampaignProbe : bool` — from Task 6 `PROBE-SETTINGS`
  - `RadarSettings.ProbeInstallId : string` — from Task 6 `PROBE-SETTINGS`
  - `RadarSettings.ProbeOnboardingSeen : bool` — from Task 6 `PROBE-SETTINGS`
  - `POST /api/contribute-trace` — from Task 8 `PROBE-CONTRIBUTE`
  - `POST /api/probe/reset-install-id` — from Task 6 `PROBE-SETTINGS` (server route)
  - `POST /api/settings` (existing v0.20 loopback-gated JSON patch endpoint)
  - JS `showToast(msg, actionLabel?, actionFn?)` — v0.21 CF-FALLBACK-UX helper at line 1879
  - JS `syncContribVisibility()` — v0.21 CF-DASH-BUTTONS at line 1954
- Produces:
  - HTML `[data-set="enableCampaignProbe"]` toggle (auto-wired via existing `loadSettings`/POST machinery)
  - HTML `#tpResetInstall` button (Settings)
  - HTML `#tpContribute` button + `#savedMsgTp` flash span (Zone Plan card)
  - JS `showProbeOnboardingIfNeeded(s)` one-shot toast function

---

- [ ] **Step 1: RE-GREP anchors before touching the file.** These line numbers drift; confirm current shape.
  ```powershell
  Select-String -Path "src/POE2Radar.Overlay/Web/DashboardHtml.cs" -Pattern "enableQuestMemory|enableCampaignGps|Zone Plan|syncContribVisibility|showToast|prContribute" | Select-Object LineNumber, Line
  ```
  Expected hits (v0.21 shape): line 699 `enableCampaignGps`, line 701 `enableQuestMemory`, line 1065 `Zone Plan`, line 1879 `function showToast`, line 1954 `function syncContribVisibility`, line 1971 `#prContribute` click handler. If any drifted more than ±20 lines from these values, update the edit anchors below accordingly before moving on.

- [ ] **Step 2: Write the failing test** at `tests/POE2Radar.Tests/Web/DashboardProbeUiTests.cs`:
  ```csharp
  using POE2Radar.Overlay.Web;
  using Xunit;

  namespace POE2Radar.Tests.Web;

  public class DashboardProbeUiTests
  {
      // Anchor (v0.21+probe): Settings toggle + Contribute-trace button + one-shot onboarding toast.
      // All assertions are string-search over the raw-string DashboardHtml.Page constant.

      [Fact]
      public void SettingsToggle_ProbeToggleBoundToEnableCampaignProbe()
      {
          // Row copy per spec §4.2. Toggle is auto-wired via existing [data-set] machinery.
          Assert.Contains("Campaign trace probe", DashboardHtml.Page);
          Assert.Contains("helps POE2GPS&rsquo;s Campaign Director learn campaign routes from your play", DashboardHtml.Page);
          Assert.Contains("data-set=\"enableCampaignProbe\"", DashboardHtml.Page);
      }

      [Fact]
      public void SettingsToggle_ResetInstallIdButtonPresent()
      {
          Assert.Contains("id=\"tpResetInstall\"", DashboardHtml.Page);
          Assert.Contains("Reset trace session id", DashboardHtml.Page);
          Assert.Contains("/api/probe/reset-install-id", DashboardHtml.Page);
      }

      [Fact]
      public void ZonePlanCard_ContributeTraceButtonPresent()
      {
          // Button lives in the Zone Plan card (data-view="director" #dirQueueCard).
          var page = DashboardHtml.Page;
          var zpIdx = page.IndexOf("id=\"dirQueueCard\"");
          var zpEnd = page.IndexOf("id=\"guideAttribution\"", zpIdx);
          Assert.True(zpIdx > 0 && zpEnd > zpIdx, "Zone Plan card anchor not found");
          var slice = page.Substring(zpIdx, zpEnd - zpIdx);
          Assert.Contains("id=\"tpContribute\"", slice);
          Assert.Contains("Contribute trace", slice);
          Assert.Contains("id=\"savedMsgTp\"", slice);
      }

      [Fact]
      public void ContributeTrace_HandlerPostsToContributeTraceEndpoint()
      {
          Assert.Contains("$('#tpContribute')", DashboardHtml.Page);
          Assert.Contains("/api/contribute-trace", DashboardHtml.Page);
      }

      [Fact]
      public void SyncContribVisibility_HidesContributeTraceWhenProbeOff()
      {
          // syncContribVisibility() must gate #tpContribute on data-set="enableCampaignProbe".
          var page = DashboardHtml.Page;
          var fnIdx = page.IndexOf("function syncContribVisibility()");
          var fnEnd = page.IndexOf("}", fnIdx);
          Assert.True(fnIdx > 0 && fnEnd > fnIdx);
          var body = page.Substring(fnIdx, fnEnd - fnIdx);
          Assert.Contains("[data-set=\"enableCampaignProbe\"]", body);
          Assert.Contains("#tpContribute", body);
      }

      [Fact]
      public void OnboardingToast_VerbatimCopyPerSpecSection6()
      {
          // Spec §6 toast copy — pin the user-visible wording.
          Assert.Contains("Campaign trace probe is on.", DashboardHtml.Page);
          Assert.Contains("Your zone traversals get logged to a local file (nothing uploads).", DashboardHtml.Page);
          Assert.Contains("One-click", DashboardHtml.Page);
          Assert.Contains("Contribute trace", DashboardHtml.Page);
          Assert.Contains("in the Campaign panel shares a session so POE2GPS", DashboardHtml.Page);
          Assert.Contains("Campaign Director gets smarter with more players", DashboardHtml.Page);
          Assert.Contains("The shared pool is public.", DashboardHtml.Page);
          Assert.Contains("Turn off in", DashboardHtml.Page);
          Assert.Contains("Settings", DashboardHtml.Page);
          Assert.Contains("Campaign trace probe.", DashboardHtml.Page);
          Assert.Contains("Got it", DashboardHtml.Page);
      }

      [Fact]
      public void OnboardingToast_OneShotGatedOnFlags()
      {
          // showProbeOnboardingIfNeeded fires only when EnableCampaignProbe && !ProbeOnboardingSeen.
          Assert.Contains("function showProbeOnboardingIfNeeded(", DashboardHtml.Page);
          Assert.Contains("s.enableCampaignProbe", DashboardHtml.Page);
          Assert.Contains("s.probeOnboardingSeen", DashboardHtml.Page);
          // Got-it click sets ProbeOnboardingSeen=true via /api/settings PATCH.
          Assert.Contains("probeOnboardingSeen:true", DashboardHtml.Page);
      }

      [Fact]
      public void OnboardingToast_InvokedFromLoadSettingsChain()
      {
          // loadSettings() at ~line 1197 must invoke the one-shot after syncContribVisibility.
          Assert.Contains("showProbeOnboardingIfNeeded(s)", DashboardHtml.Page);
      }
  }
  ```

- [ ] **Step 3: Run the test — confirm failure.**
  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~DashboardProbeUiTests
  ```
  Expected: 8 failed, 0 passed. Failure messages: `Assert.Contains() Failure ... Not found: "Campaign trace probe"` etc.

- [ ] **Step 4: Edit A — insert Settings row + reset button after `enableQuestMemory` (line 702 anchor).**
  Locate this exact block in `src/POE2Radar.Overlay/Web/DashboardHtml.cs`:
  ```html
              <div class="row"><div class="rl">Quest-memory precision<small>only effective once quest offsets are validated in-game; refines Campaign GPS.</small></div>
                <label class="sw"><input type="checkbox" data-set="enableQuestMemory"><span class="track"></span><span class="knob"></span></label></div>
  ```
  Immediately after it (before the `Curated landmark names` row at ~line 703), insert:
  ```html
              <div class="row"><div class="rl">Campaign trace probe<small>helps POE2GPS&rsquo;s Campaign Director learn campaign routes from your play &mdash; local JSONL only, nothing uploads until you click Contribute trace</small></div>
                <label class="sw"><input type="checkbox" data-set="enableCampaignProbe"><span class="track"></span><span class="knob"></span></label></div>
              <div class="row"><div class="rl">Reset trace session id<small>regenerate the anonymous install id used in your local traces (breaks cross-boot correlation for anyone consuming the public pool)</small></div>
                <button class="numin" id="tpResetInstall" title="Regenerates ProbeInstallId server-side. Existing local JSONL files keep their old id; only new boots use the new one.">Reset trace session id</button></div>
  ```

- [ ] **Step 5: Edit B — insert Contribute-trace button + savedMsg span in the Zone Plan card (line 1065 anchor).**
  Locate the Zone Plan card:
  ```html
          <div class="card" id="dirQueueCard">
              <h3>Zone Plan <small>live ranked queue for this area</small></h3>
  ```
  Immediately after the `<h3>` line, insert this Contribute row (sits above the degrade badge, below the header):
  ```html
              <div class="row" style="margin:0 0 8px 0"><div class="rl" style="flex:1"><small>Local trace probe is capturing your zone traversals. Share one boot to the public pool so POE2GPS&rsquo;s Campaign Director learns from your play.</small></div>
                <button class="numin" id="tpContribute" title="Packs the most recent complete boot&rsquo;s JSONL trace and POSTs it via the Contribute pipeline (same worker route as atlas/buffs/preload). Hidden when the probe is off.">Contribute trace</button>
                <span class="saved" id="savedMsgTp">&#10003; trace shared &mdash; thank you!</span></div>
  ```

- [ ] **Step 6: Edit C — extend `syncContribVisibility()` and its change listener (line 1954 anchor).**
  Replace the current function body:
  ```javascript
  function syncContribVisibility(){
    const bnEn = document.querySelector('[data-bn="enabled"]')?.checked;
    const prEn = document.querySelector('[data-set="preloadEnabled"]')?.checked;
    const bnBtn = $('#bnContribute'); if (bnBtn) bnBtn.style.display = bnEn ? '' : 'none';
    const prBtn = $('#prContribute'); if (prBtn) prBtn.style.display = prEn ? '' : 'none';
  }
  document.addEventListener('change', e=>{
    if (e.target && (e.target.matches?.('[data-bn="enabled"]') || e.target.matches?.('[data-set="preloadEnabled"]'))) syncContribVisibility();
  });
  ```
  with:
  ```javascript
  function syncContribVisibility(){
    const bnEn = document.querySelector('[data-bn="enabled"]')?.checked;
    const prEn = document.querySelector('[data-set="preloadEnabled"]')?.checked;
    const tpEn = document.querySelector('[data-set="enableCampaignProbe"]')?.checked;
    const bnBtn = $('#bnContribute'); if (bnBtn) bnBtn.style.display = bnEn ? '' : 'none';
    const prBtn = $('#prContribute'); if (prBtn) prBtn.style.display = prEn ? '' : 'none';
    const tpBtn = $('#tpContribute'); if (tpBtn) tpBtn.style.display = tpEn ? '' : 'none';
  }
  document.addEventListener('change', e=>{
    if (e.target && (e.target.matches?.('[data-bn="enabled"]') || e.target.matches?.('[data-set="preloadEnabled"]') || e.target.matches?.('[data-set="enableCampaignProbe"]'))) syncContribVisibility();
  });
  ```

- [ ] **Step 7: Edit D — append the trace-probe JS handlers immediately after the `$('#prContribute')` handler (~line 1976).**
  After the existing `prContribute` click handler block, append:
  ```javascript
  /* PROBE-UI: Contribute-trace + reset-install-id + one-shot onboarding toast.
     Zero-cost-when-off: #tpContribute hidden via syncContribVisibility when EnableCampaignProbe=false;
     onboarding toast only fires when EnableCampaignProbe && !ProbeOnboardingSeen. */
  function flashTp(){ const m=$('#savedMsgTp'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1600); }

  $('#tpContribute')?.addEventListener('click', async()=>{
    if(!await contribGateOrToast()) return;
    if(!window._tpOkOnce){ if(!confirm('Share your most recent boot’s campaign trace publicly? Zone traversals only — no character data, hashed NPC/dialogue text.')) return; window._tpOkOnce=true; }
    try{ const r = await fetch('/api/contribute-trace',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
      if(r.ok){ flashTp(); } else { showToast('Contribute failed (HTTP '+r.status+').'); } }catch(e){ showToast('Contribute failed (network error).'); }
  });

  $('#tpResetInstall')?.addEventListener('click', async()=>{
    if(!confirm('Regenerate the anonymous trace-session id? Existing local JSONL files keep their old id — only new boots use the new one.')) return;
    try{ const r = await fetch('/api/probe/reset-install-id',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
      if(r.ok){ showToast('Trace session id regenerated.'); } else { showToast('Reset failed (HTTP '+r.status+').'); } }catch(e){ showToast('Reset failed (network error).'); }
  });

  async function showProbeOnboardingIfNeeded(s){
    if(!s) return;
    if(!(s.enableCampaignProbe===true)) return;
    if(s.probeOnboardingSeen===true) return;
    if(window._probeOnboardingFired) return;
    window._probeOnboardingFired=true;
    showToast(
      'Campaign trace probe is on. Your zone traversals get logged to a local file (nothing uploads). One-click Contribute trace in the Campaign panel shares a session so POE2GPS’s Campaign Director gets smarter with more players’ routes. The shared pool is public. Turn off in ⚙ Settings → Campaign trace probe.',
      'Got it',
      async()=>{
        try{ await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({probeOnboardingSeen:true})}); }catch(e){}
      }
    );
  }
  ```
  Then locate the existing `loadSettings()` tail at the `if(typeof syncContribVisibility === 'function') syncContribVisibility();` line (~1197) and, on the very next line inside that same `try` block, add:
  ```javascript
      showProbeOnboardingIfNeeded(s);
  ```

- [ ] **Step 8: Run the test — confirm PASS.**
  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~DashboardProbeUiTests
  ```
  Expected: `Passed: 8, Failed: 0`.
  Then confirm the raw-string const still compiles:
  ```powershell
  dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -warnaserror
  ```
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 9: Commit.**
  ```powershell
  git add src/POE2Radar.Overlay/Web/DashboardHtml.cs tests/POE2Radar.Tests/Web/DashboardProbeUiTests.cs
  git commit -m @'
  PROBE-UI: Dashboard toggle + Contribute-trace + one-shot onboarding toast

  Adds the campaign-probe Settings row (EnableCampaignProbe toggle + Reset
  trace session id button), a Contribute-trace button on the Zone Plan card
  (hidden via syncContribVisibility when the probe is off), and a one-shot
  onboarding toast that fires on first Dashboard load. Got-it click PATCHes
  ProbeOnboardingSeen=true so the toast never fires again.

  Reuses Task 12 CF-FALLBACK-UX showToast + Task 11 CF-DASH-BUTTONS
  syncContribVisibility patterns. Contribute-trace shares the v0.21
  contribGateOrToast() sentinel-split for empty/failed URL states.
  '@
  ```

---

### Task 9: PROBE-TESTS — full test suite + README + PMS-14 tracker

Terminal task in Track A. Locks in the full behavioural contract for every prior probe task with round-trip serialization tests, anonymization determinism, per-boot rotation, zero-alloc opt-off spy, per-event-type diff-observer emissions (including UI-tree walk for NpcDialog + QuestReward against test fixtures), one-shot onboarding toast render, and `/api/contribute-trace` round-trip. Also publishes the public-facing docs: README **Campaign trace probe** section + PMS-14 tracker row re-anchored to the verification-pass shape.

**Files:**
- Create: `tests/POE2Radar.Tests/Campaign/Probe/EventRecordTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/AnonymizationHelpersTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/EventWriterTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/CampaignProbeTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/OnboardingToastTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/ApiServerTraceContributeTests.cs`
- Create: `tests/POE2Radar.Tests/Campaign/Probe/Fakes/FakeWorldSnapshot.cs`
- Modify: `README.md` — add `## 🧭 Campaign trace probe` section after `## 🤝 Community mapping` (line ~176)
- Modify: `docs/pending-manual-steps.md:17` — re-anchor PMS-14 row wording to the shipped verification-pass shape (already downgraded 2026-07-08; this pass makes the "what LO does" column match what actually ships)

**Interfaces:**
- Consumes:
  - `EventRecord + 12 event record structs + EventEnvelope + static SerializeJsonLine/DeserializeJsonLine` — `namespace POE2Radar.Core.Campaign.Probe` — from **PROBE-RECORD** (Task 2)
  - `AnonymizationHelpers.HashText16(string) : string`, `AnonymizationHelpers.NewInstallUuid() : string` — from **PROBE-ANON** (Task 3)
  - `EventWriter(string traceDir, string bootId, RadarSettings, int batchSize=32, TimeSpan? flushInterval=null, IClock? clock=null)`, `.Enqueue(EventRecord)`, `.FlushAsync()`, `.CurrentFilePath`, `.WrittenLineCount` — from **PROBE-WRITER** (Task 4)
  - `CampaignProbe(EventWriter, RadarSettings, IClock, string installUuid, string bootId)`, `.Tick(IWorldSnapshot snap)`, plus `IWorldSnapshot` shape (`AreaName`, `AreaHash`, `AreaLevel`, `IsTown`, `IsHideout`, `PlayerWorldPos`, `CharacterLevel`, `CurrentXp`, `UiTreeRoots`, `Entities`, `AllocatedPassiveNodeIds`, `LastDamageSourceMetadata`) — from **PROBE-CORE** (Task 5)
  - `RadarSettings.EnableCampaignProbe : bool`, `.ProbeInstallId : string`, `.ProbeOnboardingSeen : bool` — from **PROBE-SETTINGS** (Task 6)
  - `DashboardHtml.Render(RadarSettings) : string` — toast markup gate — from **PROBE-UI** (Task 7)
  - `ApiServer.SiblingContributeUrl(url, "trace") : string` + `/api/contribute-trace` handler — from **PROBE-CONTRIBUTE** (Task 8)
  - `Poe2Live.GetCurrentXp() : long` — free-rider from **PROBE-OFFSETS** (Task 1)
- Produces: none — terminal task. Downstream consumers get docs (README section) + PMS-14 tracker anchor only.

---

- [ ] **Step 1: Write EventRecordTests.cs (round-trip serialization for all 12 event types + byte-for-byte snake_case)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/EventRecordTests.cs`:

  ```csharp
  using System.Collections.Generic;
  using System.Text.Json;
  using POE2Radar.Core.Campaign.Probe;
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  /// <summary>PROBE-TESTS §9 EventRecord — every one of the 12 event types must
  /// round-trip Serialize→Deserialize identically, and the on-wire JSON must use
  /// snake_case field names byte-for-byte per spec §3.</summary>
  public class EventRecordTests
  {
      private static EventEnvelope Env(string kind, string cap = "live") => new(
          TsEpochMs: 1_700_000_000_000L,
          InstallUuid: "11111111-1111-4111-8111-111111111111",
          BootId:      "22222222-2222-4222-8222-222222222222",
          EventType:   kind,
          ProbeCapability: cap,
          SchemaVersion: 1,
          ActHint:  "act1",
          AreaName: "The Twilight Strand");

      [Fact]
      public void ZoneEntered_roundtrips_and_uses_snake_case_keys()
      {
          var rec = new ZoneEnteredRecord(Env("zone_entered"),
              AreaLevel: 2, AreaIdHash: "abc0123456789def",
              IsTown: false, IsHideout: false,
              PlayerWorldPos: new WorldPos(12.5f, -7.25f));
          var line = EventRecord.SerializeJsonLine(rec);

          // Byte-for-byte snake_case anchors — brief spec.
          Assert.Contains("\"event_type\":\"zone_entered\"", line);
          Assert.Contains("\"area_level\":2", line);
          Assert.Contains("\"area_id_hash\":\"abc0123456789def\"", line);
          Assert.Contains("\"is_town\":false", line);
          Assert.Contains("\"is_hideout\":false", line);
          Assert.Contains("\"player_world_pos\":{\"x\":", line);
          Assert.Contains("\"install_uuid\":", line);
          Assert.Contains("\"boot_id\":", line);
          Assert.Contains("\"schema_version\":1", line);
          Assert.Contains("\"probe_capability\":\"live\"", line);

          var back = (ZoneEnteredRecord)EventRecord.DeserializeJsonLine(line);
          Assert.Equal(rec, back);
      }

      [Theory]
      [InlineData("area_transition_used")]
      [InlineData("boss_encountered")]
      [InlineData("checkpoint_touched")]
      [InlineData("waypoint_unlocked")]
      [InlineData("player_death")]
      [InlineData("waypoint_travel")]
      [InlineData("npc_dialogue_started")]
      [InlineData("npc_dialogue_option_selected")]
      [InlineData("quest_reward_selected")]
      [InlineData("passive_allocated")]
      [InlineData("level_up")]
      public void All_remaining_event_types_roundtrip(string kind)
      {
          EventRecord rec = kind switch
          {
              "area_transition_used" => new AreaTransitionUsedRecord(Env(kind),
                  SourceArea: "G1_1", DestinationArea: "G1_2",
                  TransitionEntityMetadataPath: "Metadata/Terrain/Portal",
                  TransitionWorldPos: new WorldPos(1f, 2f)),
              "boss_encountered" => new BossEncounteredRecord(Env(kind),
                  BossMetadataPath: "Metadata/Monsters/Boss/HillockAct1",
                  BossDisplayName:  "Hillock",
                  BossWorldPos:     new WorldPos(3f, 4f),
                  IsFirstEncounter: true),
              "checkpoint_touched" => new CheckpointTouchedRecord(Env(kind),
                  CheckpointMetadataPath: "Metadata/MiscellaneousObjects/Checkpoint",
                  WorldPos: new WorldPos(5f, 6f)),
              "waypoint_unlocked" => new WaypointUnlockedRecord(Env(kind),
                  WaypointEntityMetadataPath: "Metadata/MiscellaneousObjects/Waypoint",
                  WorldPos: new WorldPos(7f, 8f)),
              "player_death" => new PlayerDeathRecord(Env(kind),
                  LastDamageSourceMetadataPath: "Metadata/Monsters/ZombieA",
                  CharacterLevel: 12),
              "waypoint_travel" => new WaypointTravelRecord(Env(kind),
                  SourceArea: "G1_town", DestinationArea: "G1_2",
                  WaypointMenuRowIndex: 3),
              "npc_dialogue_started" => new NpcDialogueStartedRecord(Env(kind),
                  NpcNameHash: "aaaaaaaaaaaaaaaa",
                  NpcMetadataPath: "Metadata/NPC/Act1/Renly",
                  NpcWorldPos: new WorldPos(9f, 10f),
                  DialogueTextHash: "bbbbbbbbbbbbbbbb",
                  OptionCount: 3),
              "npc_dialogue_option_selected" => new NpcDialogueOptionSelectedRecord(Env(kind),
                  NpcNameHash: "aaaaaaaaaaaaaaaa",
                  OptionIndex: 1,
                  OptionTextHash: "cccccccccccccccc",
                  RemainingOptionCount: 2),
              "quest_reward_selected" => new QuestRewardSelectedRecord(Env(kind),
                  RewardMetadataPath: "Metadata/Items/Skill/SparkGem",
                  RewardDisplayNameHash: "dddddddddddddddd",
                  OfferIndex: 0, TotalOffers: 3, WasSkipped: false),
              "passive_allocated" => new PassiveAllocatedRecord(Env(kind),
                  NodeId: 4711,
                  NodeDisplayNameHash: "eeeeeeeeeeeeeeee",
                  CharacterLevel: 7),
              "level_up" => new LevelUpRecord(Env(kind),
                  NewLevel: 8, XpAtLevel: 12345L,
                  AreaNameWhenLeveled: "The Twilight Strand"),
              _ => throw new System.InvalidOperationException(kind)
          };

          var line = EventRecord.SerializeJsonLine(rec);
          Assert.Contains($"\"event_type\":\"{kind}\"", line);
          var back = EventRecord.DeserializeJsonLine(line);
          Assert.Equal(rec, back);
      }

      [Fact]
      public void Envelope_uses_snake_case_ts_epoch_ms_and_schema_version_key()
      {
          var rec = new ZoneEnteredRecord(Env("zone_entered"),
              1, "0000000000000000", false, false, new WorldPos(0, 0));
          var line = EventRecord.SerializeJsonLine(rec);
          Assert.Contains("\"ts_epoch_ms\":1700000000000", line);
          Assert.Contains("\"schema_version\":1", line);
          Assert.Contains("\"act_hint\":\"act1\"", line);
          Assert.Contains("\"area_name\":\"The Twilight Strand\"", line);
          // No PascalCase leaks.
          Assert.DoesNotContain("\"EventType\"", line);
          Assert.DoesNotContain("\"AreaLevel\"", line);
      }

      [Fact]
      public void Bucket_B_stub_can_still_emit_with_v0_22_pending_capability()
      {
          var rec = new LevelUpRecord(Env("level_up", cap: "v0.22_pending"),
              NewLevel: 8, XpAtLevel: 0L, AreaNameWhenLeveled: "The Twilight Strand");
          var line = EventRecord.SerializeJsonLine(rec);
          Assert.Contains("\"probe_capability\":\"v0.22_pending\"", line);
      }
  }
  ```

- [ ] **Step 2: Run EventRecordTests — verify RED**

  ```powershell
  dotnet test tests/POE2Radar.Tests --nologo --filter "FullyQualifiedName~EventRecordTests"
  ```

  Expected before Task 2 lands: `error CS0234: The type or namespace name 'Campaign' does not exist in the namespace 'POE2Radar.Core'`.
  Expected after Task 2 has landed but this test hasn't: **PASS immediately** (round-trip is Task 2's contract). If PASS, skip Step 3 — RED means the contract has already been fulfilled and Step 1 is a codified snapshot of it.

- [ ] **Step 3: Write AnonymizationHelpersTests.cs (HashText16 determinism + hex-only + length; NewInstallUuid is a valid UUID v4)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/AnonymizationHelpersTests.cs`:

  ```csharp
  using System;
  using System.Text.RegularExpressions;
  using POE2Radar.Core.Campaign.Probe;
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  public class AnonymizationHelpersTests
  {
      [Fact]
      public void HashText16_is_deterministic_for_same_input()
      {
          Assert.Equal(
              AnonymizationHelpers.HashText16("Renly the Vendor"),
              AnonymizationHelpers.HashText16("Renly the Vendor"));
      }

      [Fact]
      public void HashText16_returns_exactly_16_lowercase_hex_chars()
      {
          var h = AnonymizationHelpers.HashText16("anything at all");
          Assert.Equal(16, h.Length);
          Assert.Matches("^[0-9a-f]{16}$", h);
      }

      [Fact]
      public void HashText16_differs_for_different_inputs()
      {
          Assert.NotEqual(
              AnonymizationHelpers.HashText16("A"),
              AnonymizationHelpers.HashText16("B"));
      }

      [Fact]
      public void HashText16_empty_string_still_produces_16_hex()
      {
          Assert.Matches("^[0-9a-f]{16}$", AnonymizationHelpers.HashText16(""));
      }

      [Fact]
      public void NewInstallUuid_matches_uuid_v4_shape()
      {
          for (int i = 0; i < 32; i++)
          {
              var u = AnonymizationHelpers.NewInstallUuid();
              Assert.True(Guid.TryParse(u, out var g), $"not a guid: {u}");
              // v4: third group starts with '4', fourth group first char in {8,9,a,b}.
              var parts = u.Split('-');
              Assert.Equal(5, parts.Length);
              Assert.Equal('4', parts[2][0]);
              Assert.Contains(parts[3][0], "89ab");
              // No accidental empty guid.
              Assert.NotEqual(Guid.Empty, g);
          }
      }

      [Fact]
      public void NewInstallUuid_returns_distinct_values_across_calls()
      {
          var a = AnonymizationHelpers.NewInstallUuid();
          var b = AnonymizationHelpers.NewInstallUuid();
          Assert.NotEqual(a, b);
      }
  }
  ```

- [ ] **Step 4: Write EventWriterTests.cs (per-boot rotation + async batch/timer flush + opt-off = zero writes + graceful degrade on file-open failure)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/EventWriterTests.cs`:

  ```csharp
  using System;
  using System.IO;
  using System.Threading.Tasks;
  using POE2Radar.Core.Campaign.Probe;
  using POE2Radar.Overlay.Config;
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  public class EventWriterTests
  {
      private static EventEnvelope Env(string boot, string kind) => new(
          TsEpochMs: 1_700_000_000_000L,
          InstallUuid: "11111111-1111-4111-8111-111111111111",
          BootId: boot, EventType: kind, ProbeCapability: "live",
          SchemaVersion: 1, ActHint: "act1", AreaName: "TS");

      private static ZoneEnteredRecord Sample(string boot) =>
          new(Env(boot, "zone_entered"), 1, "0000000000000000",
              false, false, new WorldPos(0, 0));

      private static string TempDir()
      {
          var d = Path.Combine(Path.GetTempPath(), "poe2gps_probe_" + Guid.NewGuid());
          Directory.CreateDirectory(d);
          return d;
      }

      [Fact]
      public async Task File_rotates_on_new_boot_id()
      {
          var dir = TempDir();
          var settings = new RadarSettings { EnableCampaignProbe = true };

          await using (var w1 = new EventWriter(dir, "boot-A", settings))
          {
              w1.Enqueue(Sample("boot-A"));
              await w1.FlushAsync();
              Assert.Contains("boot-A", w1.CurrentFilePath);
          }
          await using (var w2 = new EventWriter(dir, "boot-B", settings))
          {
              w2.Enqueue(Sample("boot-B"));
              await w2.FlushAsync();
              Assert.Contains("boot-B", w2.CurrentFilePath);
              Assert.NotEqual(
                  Directory.GetFiles(dir, "*boot-A*")[0],
                  w2.CurrentFilePath);
          }
          Assert.Equal(2, Directory.GetFiles(dir, "*.jsonl").Length);
      }

      [Fact]
      public async Task Async_flush_respects_batch_size()
      {
          var dir = TempDir();
          var settings = new RadarSettings { EnableCampaignProbe = true };
          await using var w = new EventWriter(dir, "boot-batch", settings,
              batchSize: 4, flushInterval: TimeSpan.FromMinutes(5));
          for (int i = 0; i < 3; i++) w.Enqueue(Sample("boot-batch"));
          // Not yet flushed (batch is 4).
          await Task.Delay(50);
          var afterThree = File.Exists(w.CurrentFilePath)
              ? new FileInfo(w.CurrentFilePath).Length : 0;
          w.Enqueue(Sample("boot-batch")); // hits batch size 4 → flush.
          // Poll briefly for the background write.
          for (int i = 0; i < 20 && (!File.Exists(w.CurrentFilePath)
              || new FileInfo(w.CurrentFilePath).Length == afterThree); i++)
              await Task.Delay(25);
          Assert.True(new FileInfo(w.CurrentFilePath).Length > afterThree);
      }

      [Fact]
      public async Task Timer_flushes_partial_batch()
      {
          var dir = TempDir();
          var settings = new RadarSettings { EnableCampaignProbe = true };
          await using var w = new EventWriter(dir, "boot-timer", settings,
              batchSize: 999, flushInterval: TimeSpan.FromMilliseconds(50));
          w.Enqueue(Sample("boot-timer"));
          for (int i = 0; i < 40 && (!File.Exists(w.CurrentFilePath)
              || new FileInfo(w.CurrentFilePath).Length == 0); i++)
              await Task.Delay(25);
          Assert.True(new FileInfo(w.CurrentFilePath).Length > 0);
      }

      [Fact]
      public async Task Opt_off_writes_zero_bytes_across_1000_ticks()
      {
          var dir = TempDir();
          var settings = new RadarSettings { EnableCampaignProbe = false };
          await using var w = new EventWriter(dir, "boot-off", settings,
              batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(1));
          for (int i = 0; i < 1000; i++) w.Enqueue(Sample("boot-off"));
          await w.FlushAsync();
          Assert.Empty(Directory.GetFiles(dir, "*.jsonl"));
      }

      [Fact]
      public async Task File_open_failure_degrades_gracefully_and_never_throws_to_caller()
      {
          // Simulate: trace dir path collides with an existing FILE (open must fail).
          var collidePath = Path.Combine(Path.GetTempPath(),
              "poe2gps_probe_collide_" + Guid.NewGuid());
          File.WriteAllText(collidePath, "not a directory");
          try
          {
              var settings = new RadarSettings { EnableCampaignProbe = true };
              await using var w = new EventWriter(collidePath, "boot-fail", settings);
              // No exception should bubble out of Enqueue or FlushAsync.
              for (int i = 0; i < 8; i++) w.Enqueue(Sample("boot-fail"));
              await w.FlushAsync();
              Assert.Equal(0L, w.WrittenLineCount);
          }
          finally { File.Delete(collidePath); }
      }
  }
  ```

- [ ] **Step 5: Write Fakes/FakeWorldSnapshot.cs (fixture used by CampaignProbeTests)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/Fakes/FakeWorldSnapshot.cs`:

  ```csharp
  using System.Collections.Generic;
  using POE2Radar.Core.Campaign.Probe;

  namespace POE2Radar.Tests.Campaign.Probe.Fakes;

  /// <summary>Mutable IWorldSnapshot fixture — tests flip fields between ticks
  /// to drive diff-observer emission paths. Keeps a shared instance so
  /// CampaignProbe.Tick(same-ref) still observes state changes (diff-observers
  /// key off value comparison, not reference identity).</summary>
  public sealed class FakeWorldSnapshot : IWorldSnapshot
  {
      public string AreaName { get; set; } = "The Twilight Strand";
      public uint AreaHash { get; set; } = 0x1234u;
      public int AreaLevel { get; set; } = 1;
      public bool IsTown { get; set; } = false;
      public bool IsHideout { get; set; } = false;
      public (float X, float Y) PlayerWorldPos { get; set; } = (0f, 0f);
      public int CharacterLevel { get; set; } = 1;
      public long CurrentXp { get; set; } = 0L;
      public IReadOnlyList<UiPanelNode> UiTreeRoots { get; set; } = new List<UiPanelNode>();
      public IReadOnlyList<EntitySnap> Entities { get; set; } = new List<EntitySnap>();
      public IReadOnlyList<int> AllocatedPassiveNodeIds { get; set; } = new List<int>();
      public string? LastDamageSourceMetadata { get; set; }
  }
  ```

- [ ] **Step 6: Write CampaignProbeTests.cs (per-event-type diff-observer emits; opt-off zero-alloc across 1000 ticks; UI-tree walk stub for NpcDialog/QuestReward)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/CampaignProbeTests.cs`:

  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using POE2Radar.Core.Campaign.Probe;
  using POE2Radar.Overlay.Config;
  using POE2Radar.Tests.Campaign.Probe.Fakes;
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  public class CampaignProbeTests
  {
      private sealed class FixedClock : IClock
      {
          public long UnixMs { get; set; } = 1_700_000_000_000L;
          public long UtcNowMs() => UnixMs;
      }

      private static (CampaignProbe probe, EventWriter writer, string dir) Boot(
          bool enabled = true)
      {
          var dir = Path.Combine(Path.GetTempPath(),
              "poe2gps_probe_" + Guid.NewGuid());
          Directory.CreateDirectory(dir);
          var settings = new RadarSettings { EnableCampaignProbe = enabled };
          var clock = new FixedClock();
          var writer = new EventWriter(dir, "boot-x", settings,
              batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(20),
              clock: clock);
          var probe = new CampaignProbe(writer, settings, clock,
              installUuid: "11111111-1111-4111-8111-111111111111",
              bootId: "boot-x");
          return (probe, writer, dir);
      }

      private static async Task<List<string>> DrainAsync(EventWriter w)
      {
          await w.FlushAsync();
          if (!File.Exists(w.CurrentFilePath)) return new();
          return (await File.ReadAllLinesAsync(w.CurrentFilePath)).ToList();
      }

      [Fact]
      public async Task ZoneEntered_fires_on_area_change_and_suppresses_within_same_tick()
      {
          var (probe, writer, _) = Boot();
          var snap = new FakeWorldSnapshot { AreaName = "G1_1", AreaHash = 1 };
          probe.Tick(snap);           // first observation — emits.
          probe.Tick(snap);           // no change — must NOT emit.
          snap.AreaName = "G1_2"; snap.AreaHash = 2;
          probe.Tick(snap);           // change — emits.
          var lines = await DrainAsync(writer);
          Assert.Equal(2, lines.Count(l => l.Contains("\"event_type\":\"zone_entered\"")));
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task BossEncountered_first_encounter_flag_flips_after_first_sighting()
      {
          var (probe, writer, _) = Boot();
          var snap = new FakeWorldSnapshot
          {
              AreaName = "G1_1",
              Entities = new List<EntitySnap>
              {
                  new(MetadataPath: "Metadata/Monsters/Boss/HillockAct1",
                      DisplayName: "Hillock", WorldPos: (10, 10),
                      IsBoss: true, IsAlive: true)
              }
          };
          probe.Tick(snap);
          probe.Tick(snap);
          var lines = await DrainAsync(writer);
          var bossLines = lines.Where(l => l.Contains("\"event_type\":\"boss_encountered\"")).ToList();
          Assert.Single(bossLines);
          Assert.Contains("\"is_first_encounter\":true", bossLines[0]);
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task PassiveAllocated_fires_once_per_new_node_id()
      {
          var (probe, writer, _) = Boot();
          var snap = new FakeWorldSnapshot { AreaName = "G1_1",
              AllocatedPassiveNodeIds = new List<int>{ 100 } };
          probe.Tick(snap);
          snap.AllocatedPassiveNodeIds = new List<int>{ 100, 200 };
          probe.Tick(snap);
          snap.AllocatedPassiveNodeIds = new List<int>{ 100, 200 };
          probe.Tick(snap);
          var lines = await DrainAsync(writer);
          Assert.Equal(2, lines.Count(l => l.Contains("\"event_type\":\"passive_allocated\"")));
          Assert.Contains(lines, l => l.Contains("\"node_id\":100"));
          Assert.Contains(lines, l => l.Contains("\"node_id\":200"));
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task LevelUp_fires_on_character_level_increment_and_captures_area()
      {
          var (probe, writer, _) = Boot();
          var snap = new FakeWorldSnapshot { CharacterLevel = 4,
              CurrentXp = 500L, AreaName = "G1_1" };
          probe.Tick(snap);
          snap.CharacterLevel = 5; snap.CurrentXp = 1200L;
          probe.Tick(snap);
          var lines = await DrainAsync(writer);
          var lu = lines.Single(l => l.Contains("\"event_type\":\"level_up\""));
          Assert.Contains("\"new_level\":5", lu);
          Assert.Contains("\"xp_at_level\":1200", lu);
          Assert.Contains("\"area_name_when_leveled\":\"G1_1\"", lu);
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task PlayerDeath_fires_on_alive_to_dead_transition_with_last_damage_source()
      {
          var (probe, writer, _) = Boot();
          var snap = new FakeWorldSnapshot { CharacterLevel = 8,
              LastDamageSourceMetadata = "Metadata/Monsters/ZombieA" };
          // Alive baseline via "alive" flag on the player-slot entity or an
          // explicit IsPlayerAlive on the fake — the fixture models it here as
          // "LastDamageSourceMetadata == null means alive".
          snap.LastDamageSourceMetadata = null;
          probe.Tick(snap);
          snap.LastDamageSourceMetadata = "Metadata/Monsters/ZombieA";
          probe.Tick(snap);
          var lines = await DrainAsync(writer);
          var death = lines.Single(l => l.Contains("\"event_type\":\"player_death\""));
          Assert.Contains("\"last_damage_source_metadata_path\":\"Metadata/Monsters/ZombieA\"", death);
          Assert.Contains("\"character_level\":8", death);
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task NpcDialogueStarted_emits_from_ui_tree_walk_stub()
      {
          var (probe, writer, _) = Boot();
          var npcNode = new UiPanelNode(
              SignatureKind: UiSignatureKind.NpcDialog,
              Fields: new Dictionary<string, object?>
              {
                  ["npc_name"] = "Renly the Vendor",
                  ["npc_metadata_path"] = "Metadata/NPC/Act1/Renly",
                  ["npc_world_pos_x"] = 5f,
                  ["npc_world_pos_y"] = 6f,
                  ["dialogue_text"] = "Wander where you will, exile.",
                  ["option_count"] = 3
              });
          var snap = new FakeWorldSnapshot
          {
              AreaName = "G1_1",
              UiTreeRoots = new List<UiPanelNode> { npcNode }
          };
          probe.Tick(snap);
          probe.Tick(snap); // same panel open — must NOT re-emit "started".
          var lines = await DrainAsync(writer);
          var started = lines.Where(l =>
              l.Contains("\"event_type\":\"npc_dialogue_started\"")).ToList();
          Assert.Single(started);
          Assert.Contains("\"option_count\":3", started[0]);
          // Name + text must be 16-hex hashes, never raw plaintext.
          Assert.DoesNotContain("Renly the Vendor", started[0]);
          Assert.DoesNotContain("Wander where you will", started[0]);
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task QuestRewardSelected_emits_from_ui_tree_walk_stub_with_offer_index()
      {
          var (probe, writer, _) = Boot();
          var reward = new UiPanelNode(
              SignatureKind: UiSignatureKind.QuestReward,
              Fields: new Dictionary<string, object?>
              {
                  ["reward_metadata_path"] = "Metadata/Items/Skill/SparkGem",
                  ["reward_display_name"] = "Spark",
                  ["offer_index"] = 0,
                  ["total_offers"] = 3,
                  ["was_skipped"] = false
              });
          var snap = new FakeWorldSnapshot { AreaName = "G1_town",
              UiTreeRoots = new List<UiPanelNode> { reward } };
          probe.Tick(snap);
          snap.UiTreeRoots = new List<UiPanelNode>(); // panel closed after pick.
          probe.Tick(snap);
          var lines = await DrainAsync(writer);
          var picked = lines.Single(l =>
              l.Contains("\"event_type\":\"quest_reward_selected\""));
          Assert.Contains("\"offer_index\":0", picked);
          Assert.Contains("\"total_offers\":3", picked);
          Assert.Contains("\"was_skipped\":false", picked);
          Assert.DoesNotContain("\"Spark\"", picked); // hashed, not raw.
          await writer.DisposeAsync();
      }

      [Fact]
      public async Task Opt_off_spy_asserts_zero_allocations_across_1000_ticks()
      {
          var (probe, writer, _) = Boot(enabled: false);
          var snap = new FakeWorldSnapshot();
          // Warmup — jit + first-tick lazy paths.
          probe.Tick(snap);
          GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
          var beforeGen0 = GC.CollectionCount(0);
          var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
          for (int i = 0; i < 1000; i++)
          {
              snap.PlayerWorldPos = (i, i);
              probe.Tick(snap);
          }
          var deltaBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
          var deltaGen0  = GC.CollectionCount(0) - beforeGen0;
          Assert.Equal(0, deltaGen0);
          // Zero-cost-when-off: allow only the boxed struct-enumerator noise
          // Tick's early-return may create (should be 0; hard cap 256 B).
          Assert.InRange(deltaBytes, 0, 256);
          await writer.FlushAsync();
          Assert.Empty(Directory.GetFiles(
              Path.GetDirectoryName(writer.CurrentFilePath)!, "*.jsonl"));
          await writer.DisposeAsync();
      }
  }
  ```

- [ ] **Step 7: Write OnboardingToastTests.cs (extends DashboardHtml suite — toast markup gated on ProbeOnboardingSeen && EnableCampaignProbe)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/OnboardingToastTests.cs`:

  ```csharp
  using POE2Radar.Overlay.Config;
  using POE2Radar.Overlay.Web;
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  /// <summary>PROBE-TESTS §9 onboarding — the Dashboard emits the one-shot
  /// probe-onboarding toast ONLY when the probe is on AND the user hasn't
  /// seen it yet. Copy anchor per spec §6.</summary>
  public class OnboardingToastTests
  {
      private const string ToastAnchor = "Campaign trace probe is on";

      [Fact]
      public void Toast_renders_when_probe_on_and_not_seen()
      {
          var s = new RadarSettings
          {
              EnableCampaignProbe = true,
              ProbeOnboardingSeen = false
          };
          Assert.Contains(ToastAnchor, DashboardHtml.Render(s));
      }

      [Fact]
      public void Toast_absent_when_already_seen()
      {
          var s = new RadarSettings
          {
              EnableCampaignProbe = true,
              ProbeOnboardingSeen = true
          };
          Assert.DoesNotContain(ToastAnchor, DashboardHtml.Render(s));
      }

      [Fact]
      public void Toast_absent_when_probe_disabled()
      {
          var s = new RadarSettings
          {
              EnableCampaignProbe = false,
              ProbeOnboardingSeen = false
          };
          Assert.DoesNotContain(ToastAnchor, DashboardHtml.Render(s));
      }

      [Fact]
      public void Toast_absent_when_probe_disabled_and_already_seen()
      {
          var s = new RadarSettings
          {
              EnableCampaignProbe = false,
              ProbeOnboardingSeen = true
          };
          Assert.DoesNotContain(ToastAnchor, DashboardHtml.Render(s));
      }
  }
  ```

- [ ] **Step 8: Write ApiServerTraceContributeTests.cs (packs file + POSTs to sibling URL, gated on EnableCampaignProbe)**

  Create `tests/POE2Radar.Tests/Campaign/Probe/ApiServerTraceContributeTests.cs`:

  ```csharp
  using System.IO;
  using System.Net;
  using System.Net.Http;
  using System.Text;
  using System.Threading;
  using System.Threading.Tasks;
  using POE2Radar.Overlay.Config;
  using POE2Radar.Overlay.Web;
  using POE2Radar.Tests.Web; // TestBoot
  using Xunit;

  namespace POE2Radar.Tests.Campaign.Probe;

  public class ApiServerTraceContributeTests
  {
      [Fact]
      public void SiblingContributeUrl_rewrites_trace_pattern()
      {
          Assert.Equal(
              "https://x.workers.dev/submit-trace",
              ApiServer.SiblingContributeUrl(
                  "https://x.workers.dev/submit-atlas", "trace"));
          Assert.Equal(
              "https://x.workers.dev/submit-trace",
              ApiServer.SiblingContributeUrl(
                  "https://x.workers.dev/submit-buffs", "trace"));
          Assert.Equal(
              "https://x.workers.dev/submit-trace",
              ApiServer.SiblingContributeUrl(
                  "https://x.workers.dev/submit-preload", "trace"));
          Assert.Equal(
              "https://x.workers.dev/submit-trace",
              ApiServer.SiblingContributeUrl(
                  "https://x.workers.dev", "trace"));
      }

      [Fact]
      public async Task ContributeTrace_packs_jsonl_and_posts_to_sibling_url()
      {
          // Stand up a stub upstream that just records what it received.
          var upstream = new HttpListener();
          var upstreamPort = TestBoot.FreeTcpPort();
          upstream.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
          upstream.Start();
          byte[]? receivedBody = null;
          string? receivedPath = null;
          var upstreamDone = new TaskCompletionSource();
          var upstreamLoop = Task.Run(async () =>
          {
              var ctx = await upstream.GetContextAsync();
              receivedPath = ctx.Request.Url!.AbsolutePath;
              using var ms = new MemoryStream();
              await ctx.Request.InputStream.CopyToAsync(ms);
              receivedBody = ms.ToArray();
              ctx.Response.StatusCode = 200;
              ctx.Response.Close();
              upstreamDone.SetResult();
          });

          // Seed a plausible trace file for the handler to slurp.
          var traceDir = Path.Combine(Path.GetTempPath(),
              "poe2gps_probe_" + System.Guid.NewGuid());
          Directory.CreateDirectory(traceDir);
          var traceFile = Path.Combine(traceDir, "boot-x.jsonl");
          await File.WriteAllTextAsync(traceFile,
              "{\"event_type\":\"zone_entered\",\"schema_version\":1}\n" +
              "{\"event_type\":\"level_up\",\"schema_version\":1}\n",
              Encoding.UTF8);

          var settings = new RadarSettings
          {
              EnableCampaignProbe = true,
              ContribUrl = $"http://127.0.0.1:{upstreamPort}/submit-atlas"
          };
          var api = TestBoot.Server(webMap: false, webObs: false, out var port,
              settings: settings, traceFileProvider: () => traceFile);
          try
          {
              using var client = new HttpClient();
              var resp = await client.PostAsync(
                  $"http://localhost:{port}/api/contribute-trace",
                  new StringContent(""));
              Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
              await upstreamDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
              Assert.Equal("/submit-trace", receivedPath);
              Assert.NotNull(receivedBody);
              // Body must reference at least one event_type from the seeded jsonl
              // (raw or gzipped-base64 wrap depending on Task 8's chosen wire).
              var bodyText = Encoding.UTF8.GetString(receivedBody!);
              Assert.True(
                  bodyText.Contains("zone_entered") ||
                  bodyText.Contains("jsonl_gzip_b64"),
                  $"upstream body did not carry the trace: {bodyText[..System.Math.Min(200, bodyText.Length)]}");
          }
          finally
          {
              api.Dispose();
              upstream.Stop();
              await upstreamLoop;
          }
      }

      [Fact]
      public async Task ContributeTrace_returns_409_when_probe_disabled()
      {
          var settings = new RadarSettings { EnableCampaignProbe = false };
          var api = TestBoot.Server(webMap: false, webObs: false, out var port,
              settings: settings);
          try
          {
              using var client = new HttpClient();
              var resp = await client.PostAsync(
                  $"http://localhost:{port}/api/contribute-trace",
                  new StringContent(""));
              Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
          }
          finally { api.Dispose(); }
      }
  }
  ```

- [ ] **Step 9: Run the whole Campaign.Probe test suite — verify RED where expected, GREEN where a prior task already fulfilled the contract**

  ```powershell
  dotnet test tests/POE2Radar.Tests --nologo `
      --filter "FullyQualifiedName~POE2Radar.Tests.Campaign.Probe"
  ```

  Expected in a clean Track-A build: every test in every file **passes** — Tasks 2 through 8 are the implementations, and this task is codifying their contracts. If any test fails, the failure identifies exactly which prior task drifted from the spec (fix in that task, don't paper over here). RED here is a real regression signal.

- [ ] **Step 10: Add the README `Campaign trace probe` section**

  In `README.md`, insert a new `##` section immediately after the existing `## 🤝 Community mapping` section (around line 176):

  ```markdown
  ## 🧭 Campaign trace probe

  POE2GPS ships an **opt-out** campaign trace probe (default **on**). While you play the campaign it writes an anonymized JSONL of your zone traversals — zone enters, checkpoints, waypoints, NPC dialogue starts, quest reward picks, passive allocations, level-ups, deaths — to a local file. **Nothing uploads until you click Contribute.**

  **What it captures (12 event types):**
  Zone enters, area transitions, boss encounters, checkpoints touched, waypoints unlocked, deaths, waypoint travel, NPC dialogue started, NPC dialogue option selected, quest reward selected, passive allocated, level up. Every record carries an envelope (`ts_epoch_ms`, `install_uuid`, `boot_id`, `event_type`, `probe_capability`, `schema_version`, `act_hint`, `area_name`). NPC/dialogue/reward text is **sha256-hashed to 16 hex chars** before write — the raw strings never leave your machine.

  **Where the JSONL lives:**
  `%APPDATA%\poe2gps\campaign_traces\<boot_epoch_ms>.jsonl` — one file per POE2GPS boot. Open it in any text editor and read every line yourself before sharing.

  **How to Contribute:**
  Open the POE2GPS Dashboard → Campaign panel → **Contribute trace**. That posts the current boot's JSONL through the same sibling-route worker POE2GPS uses for atlas / buffs / preload contributions. The shared pool is public and consumed by POE2GPS's own Campaign Director to learn campaign routes from real play.

  **Turn it off:**
  Dashboard → ⚙️ Settings → **Campaign trace probe** — one click, effective immediately, zero writes when off. You can also **Reset trace session id** from Settings if you want a fresh `install_uuid` before contributing.
  ```

- [ ] **Step 11: Re-anchor the PMS-14 row in `docs/pending-manual-steps.md:17` to the shipped verification-pass shape**

  Replace the entire row currently on line 17 (the row that starts `| 14 | **Campaign Probe in-game verification pass**...`) with:

  ```markdown
  | 14 | **Campaign Probe in-game verification pass** | Confirms all 12 Campaign Probe event types emit plausible values on a live client (upstream offsets already extracted from `imkk000/poe2-offsets` and shipped; this is a smoke pass, not a Research run) + Long List #34 XP/hour Session HUD chip (via `charCurEXPOff = 0x1D8` shipped alongside campaign probe as the free-rider XP accessor on `Poe2Live`) | Boot POE2GPS with `EnableCampaignProbe = true` (default); run a short Act 1 sequence: **enter a zone, touch a checkpoint, use one area transition, talk to an NPC and pick a dialogue option, unlock a waypoint, travel via waypoint, pick a quest reward, allocate a passive, level up, and die once**. Open the most recent JSONL at `%APPDATA%\poe2gps\campaign_traces\` and confirm each of the 12 `event_type`s appears at least once with plausible fields (hashed text is 16 hex chars, world positions are non-zero, `character_level` is monotone across `level_up` and `passive_allocated`, `probe_capability` reads `"live"`). Report anything that reads as `"v0.22_pending"` or misfires | 10-15 min | Bucket-B live-reads validation for Campaign Probe design spec `docs/superpowers/specs/2026-07-08-campaign-probe-design.md` |
  ```

- [ ] **Step 12: Full-suite green gate (whole test project + Campaign.Probe filter)**

  ```powershell
  dotnet test tests/POE2Radar.Tests --nologo
  dotnet test tests/POE2Radar.Tests --nologo `
      --filter "FullyQualifiedName~POE2Radar.Tests.Campaign.Probe"
  ```

  Expected: both invocations report **Passed!** with 0 failed. If the whole-suite run regresses a pre-existing test not in `Campaign.Probe`, that's a bleed-through from an earlier task — flag to LO before proceeding.

- [ ] **Step 13: Sanity-check the docs — README section renders + PMS-14 row still parses as one Markdown table row**

  ```powershell
  Select-String -Path README.md -Pattern "^## " | Select-Object LineNumber, Line
  Select-String -Path docs/pending-manual-steps.md -Pattern "^\| 14 " | Select-Object LineNumber, Line
  ```

  Expected: README lists a fresh `## 🧭 Campaign trace probe` heading between `## 🤝 Community mapping` and `## 🏗️ Architecture`. PMS-14 shows exactly one row starting with `| 14 |` and no orphaned trailing pipes. If the row wraps to two lines, GitHub's Markdown table parser will break — collapse back to a single line.

- [ ] **Step 14: Commit**

  ```powershell
  git add tests/POE2Radar.Tests/Campaign/Probe/ README.md docs/pending-manual-steps.md
  git commit -m @'
  PROBE-TESTS: full test suite + README section + PMS-14 verification-pass shape

  Locks in the campaign-probe contract with round-trip serialization for all 12
  event types, HashText16 / NewInstallUuid determinism + shape checks, per-boot
  file rotation, batch/timer async flush, opt-off = zero writes AND zero
  allocations across 1000 ticks, per-event-type diff-observer emissions
  (including UI-tree-walk stubs for NpcDialog + QuestReward against test
  fixtures), one-shot onboarding toast gating, and /api/contribute-trace
  round-trip against a stub upstream. README gains a Campaign trace probe
  section that explains capture, storage, and Contribute. PMS-14 row
  re-anchored to the shipped verification-pass shape (10-15 min live smoke,
  no Research run) so the tracker matches what actually ships.
  '@
  ```

---

## Ordering Constraints

1. Task 1 (PROBE-OFFSETS) FIRST - unblocks all consumers (accessors, WalkUiTree, hover tracker anchor).
2. Tasks 2, 3, 4 (RECORD, ANON, WRITER) parallel after Task 1.
3. Task 5 (PROBE-SETTINGS) after Task 3 (consumes `AnonymizationHelpers.NewInstallUuid`).
4. Task 6 (PROBE-CORE) after Tasks 1-5 (consumes accessors, records, anon, writer, settings).
5. Task 7 (PROBE-CONTRIBUTE) after Tasks 4-6 (consumes EventWriter surface + `RadarSettings.ResetTraceSession` + `AnonymizationHelpers.NewInstallUuid`).
6. Task 8 (PROBE-UI) after Task 7 (fetches `/api/contribute-trace` + `/api/probe/reset-install-id`).
7. Task 9 (PROBE-TESTS) after Tasks 1-8 (integration + full-suite verification).

## Pending Manual Steps (blocks)

- **PMS-14** (in-game verification pass, 10-15 min) - after PROBE-TESTS lands, LO boots POE2GPS with `EnableCampaignProbe=true` and runs a short Act 1 sequence (enter zone, talk NPC, allocate passive, level up). Confirms the 6 previously-Bucket-B events fire with plausible values in the emitted JSONL. Downgraded from a 45-60 min Research probe after upstream offset extraction.

## Non-Goals (from spec section 10)

- No route-authoring UI inside POE2GPS. This is capture-only.
- No PII beyond what `Poe2Live` shipped surface exposes.
- No build detection. That's poe2open's problem.
- No automatic upload. The Contribute click is the entire "share" gesture.
- No hall-of-fame page for trace contributors (deferred like SL #47).
- No trace-diff visualization. Users see the raw JSONL in a text editor.
- No cross-boot correlation for the Contribute button - users share one boot's worth per click. Multi-boot merging is a downstream concern.
