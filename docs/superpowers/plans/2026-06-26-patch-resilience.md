# Patch-Resilience & Health/Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a PoE2 patch breaks the offsets, POE2GPS self-detects it, keeps running, and shows a clear update-aware banner + dashboard Status panel — instead of dying at startup or going silently blank.

**Architecture:** A pure, clock-injected `OffsetHealthMonitor` (Core) runs a six-state machine off a per-tick graduated chain probe. `Poe2Live.Probe()` reports how far the chain resolved (anchored on the patch-stable `AreaHash` low field). `RadarApp` starts immediately and resolves the GameState slot lazily on a background thread (so launching at login / on a broken patch no longer exits), feeding the monitor each world tick and publishing health to the overlay banner, `/state`, and the dashboard.

**Tech Stack:** .NET 10, C# (net10.0-windows, x64), xUnit, Vortice.Direct2D1, vanilla-JS dashboard embedded in `DashboardHtml.cs`.

## Global Constraints

- **Strictly read-only.** No `SendInput`/`PostMessage`/`SendMessage`/`keybd_event`/`mouse_event`, no `WriteProcessMemory`/`VirtualProtectEx`/`CreateRemoteThread`/injection. `OpenProcess` stays `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`. Do not name any method/variable/comment with a forbidden symbol (the compliance gate is a word-boundary regex).
- **Banner/message strings are plain English** and must contain none of those forbidden symbol names. They come from a fixed enum/branch mapping in the monitor — never raw memory addresses or exception text (they ride the unauthenticated `localhost /state`).
- **One Core enum** `POE2Radar.Core.Health.HealthState { Waiting, Searching, Ok, Loading, NotInGame, Broken }` — used by the monitor, `RadarState`, `RenderContext`, and `/state` (serialized lowercase). No second overlay-side enum.
- **S4 (in-zone) gate = `AreaHash@+0x11C` ≠ 0 AND `AreaLevel@+0xC4` ∈ [0..100] inclusive.** `AreaCode` is advisory only (two-hop read), never a gate. `AreaLevel == 0` is a legitimate hideout value.
- **Thresholds (the monitor's shipping defaults):** S4 stability 3 ticks · Loading→Broken hold-off 25 s · post-`Ok` offline warn 5 min · radar-empty warn 10 ticks · sustained-search hint 90 s · pattern-broke hint 15 s · AOB retry cadence 1.5 s.
- **CI must stay green:** `scripts/compliance-gate.ps1`, `scripts/scrub-strings.ps1 -SelfTest`, full xUnit suite. `tests/POE2Radar.Tests` references **Core only** (the monitor lives in Core so it is unit-testable; `Poe2Live`/overlay code is integration/manual-tested, matching the existing pattern).
- Re-attach (Task 7) is the only higher-risk piece and is **cuttable** — Tasks 1–6 deliver the full feature without it.

---

### Task 1: Core — `OffsetHealthMonitor` (pure state machine) + types

**Files:**
- Create: `src/POE2Radar.Core/Health/OffsetHealthMonitor.cs`
- Test: `tests/POE2Radar.Tests/OffsetHealthMonitorTests.cs`

**Interfaces:**
- Produces: `enum ResolveStage { None, GameState, InGameState, AreaInstance, InZone, Full }`; `enum HealthState { Waiting, Searching, Ok, Loading, NotInGame, Broken }`; `readonly record struct ChainProbe(bool Attached, bool SlotResolved, int AobCandidateCount, bool AobScanned, ResolveStage Stage, bool TerrainPresent, bool UpdateAvailable, bool UpdateChecked, string? UpdateUrl)`; `readonly record struct HealthVerdict(HealthState State, string? Message)`; `class OffsetHealthMonitor` with `HealthVerdict Evaluate(ChainProbe p, DateTime now)` and `static OffsetHealthMonitor CreateDefault()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/POE2Radar.Tests/OffsetHealthMonitorTests.cs`:

```csharp
using System;
using POE2Radar.Core.Health;
using Xunit;

namespace POE2Radar.Tests;

public class OffsetHealthMonitorTests
{
    // Shipping thresholds, with a fake clock (seconds).
    private static OffsetHealthMonitor New() =>
        new(TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90), 3, 10);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int sec) => T0.AddSeconds(sec);

    private static ChainProbe P(bool attached, bool slot, ResolveStage stage,
        int aob = 1, bool aobScanned = true, bool terrain = true,
        bool updAvail = false, bool updChecked = true, string? url = "https://rel") =>
        new(attached, slot, aob, aobScanned, stage, terrain, updAvail, updChecked, url);

    [Fact] public void Not_attached_is_Waiting()
    {
        Assert.Equal(HealthState.Waiting, New().Evaluate(P(false, false, ResolveStage.None), At(0)).State);
    }

    [Fact] public void Attached_no_slot_is_Searching_connecting()
    {
        var v = New().Evaluate(P(true, false, ResolveStage.None), At(0));
        Assert.Equal(HealthState.Searching, v.State);
        Assert.Contains("Connecting", v.Message);
    }

    [Fact] public void Searching_sustained_shows_soft_update_hint()
    {
        var m = New();
        m.Evaluate(P(true, false, ResolveStage.None), At(0));
        var v = m.Evaluate(P(true, false, ResolveStage.None), At(91));   // > 90 s
        Assert.Equal(HealthState.Searching, v.State);
        Assert.Contains("may need an update", v.Message);
    }

    [Fact] public void Aob_zero_after_15s_is_pattern_broke_wording()
    {
        var m = New();
        m.Evaluate(P(true, false, ResolveStage.None, aob: 0), At(0));
        var v = m.Evaluate(P(true, false, ResolveStage.None, aob: 0), At(16));
        Assert.Contains("can't find", v.Message);
    }

    [Fact] public void Full_is_Ok_with_no_message()
    {
        var v = New().Evaluate(P(true, true, ResolveStage.Full), At(0));
        Assert.Equal(HealthState.Ok, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Ok_but_no_terrain_for_ten_ticks_soft_warns()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 10; i++) v = m.Evaluate(P(true, true, ResolveStage.Full, terrain: false), At(i));
        Assert.Equal(HealthState.Ok, v.State);
        Assert.Contains("no map data", v.Message);
    }

    [Fact] public void InZone_below_holdoff_is_Loading_no_alarm()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 3; i++) v = m.Evaluate(P(true, true, ResolveStage.InZone), At(i));
        Assert.Equal(HealthState.Loading, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Single_InZone_tick_not_trusted_stays_Searching()
    {
        var v = New().Evaluate(P(true, true, ResolveStage.InZone), At(0));
        Assert.Equal(HealthState.Searching, v.State);
    }

    [Fact] public void InZone_sustained_past_holdoff_is_Broken()
    {
        var m = New(); HealthVerdict v = default;
        for (var i = 0; i < 3; i++) v = m.Evaluate(P(true, true, ResolveStage.InZone), At(i)); // loadingSince=At(2)
        v = m.Evaluate(P(true, true, ResolveStage.InZone), At(27));   // 25 s later
        Assert.Equal(HealthState.Broken, v.State);
        Assert.Contains("can't read", v.Message);
    }

    [Fact] public void Holdoff_resets_after_returning_to_Ok()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone), At(i)); // loadingSince=At(2)
        m.Evaluate(P(true, true, ResolveStage.Full), At(20));                               // Ok clears loadingSince
        var v = m.Evaluate(P(true, true, ResolveStage.InZone), At(21));                     // loadingSince=At(21)
        v = m.Evaluate(P(true, true, ResolveStage.InZone), At(40));                         // 19 s < 25 → not Broken
        Assert.Equal(HealthState.Loading, v.State);
    }

    [Fact] public void Post_ok_offline_under_five_min_is_benign()
    {
        var m = New();
        m.Evaluate(P(true, true, ResolveStage.Full), At(0));                       // Ok latch
        var v = m.Evaluate(P(true, true, ResolveStage.InGameState), At(60));       // < 5 min
        Assert.Equal(HealthState.NotInGame, v.State);
        Assert.Null(v.Message);
    }

    [Fact] public void Post_ok_offline_over_five_min_soft_warns()
    {
        var m = New();
        m.Evaluate(P(true, true, ResolveStage.Full), At(0));                       // Ok latch
        m.Evaluate(P(true, true, ResolveStage.InGameState), At(1));                // NotInGame since At(1)
        var v = m.Evaluate(P(true, true, ResolveStage.InGameState), At(302));      // > 5 min
        Assert.Equal(HealthState.NotInGame, v.State);
        Assert.NotNull(v.Message);
    }

    [Fact] public void Broken_message_is_update_aware_when_update_available()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone, updAvail: true, url: "http://dl"), At(i));
        var v = m.Evaluate(P(true, true, ResolveStage.InZone, updAvail: true, url: "http://dl"), At(30));
        Assert.Equal(HealthState.Broken, v.State);
        Assert.Contains("Update available", v.Message);
        Assert.Contains("http://dl", v.Message);
    }

    [Fact] public void Broken_message_when_check_not_done_says_likely_updated()
    {
        var m = New();
        for (var i = 0; i < 3; i++) m.Evaluate(P(true, true, ResolveStage.InZone, updChecked: false), At(i));
        var v = m.Evaluate(P(true, true, ResolveStage.InZone, updChecked: false), At(30));
        Assert.Contains("likely just updated", v.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter OffsetHealthMonitorTests`
Expected: FAIL — `OffsetHealthMonitor` / the `Health` namespace do not exist (compile error).

- [ ] **Step 3: Implement the monitor**

Create `src/POE2Radar.Core/Health/OffsetHealthMonitor.cs`:

```csharp
namespace POE2Radar.Core.Health;

/// <summary>How far the GameState → InGameState → AreaInstance → LocalPlayer chain resolved on one tick.</summary>
public enum ResolveStage
{
    None         = 0,  // GameState slot deref failed (slot 0 / process gone)
    GameState    = 1,  // GameState readable
    InGameState  = 2,  // a state-object pointer is present
    AreaInstance = 3,  // AreaInstance pointer non-null
    InZone       = 4,  // S4: AreaHash != 0 && AreaLevel in [0..100] — provably in a zone
    Full         = 5,  // S5: LocalPlayer present + metadata "Metadata/" — full success
}

/// <summary>Coarse health the overlay banner + dashboard surface.</summary>
public enum HealthState { Waiting, Searching, Ok, Loading, NotInGame, Broken }

/// <summary>One world-tick of observations. Pure data — no memory handles.</summary>
public readonly record struct ChainProbe(
    bool Attached,            // PoE2 process is alive (resolver)
    bool SlotResolved,        // resolver has published an in-zone-validated slot this attach
    int AobCandidateCount,    // raw AOB candidate count from the last scan (0 = pattern matched nothing)
    bool AobScanned,          // the resolver has completed at least one AOB scan
    ResolveStage Stage,       // how far Probe() got this tick on the resolved slot
    bool TerrainPresent,      // terrain grid read OK this tick (only meaningful at Full)
    bool UpdateAvailable,     // UpdateChecker: a newer release exists
    bool UpdateChecked,       // UpdateChecker: the check completed (we know current-vs-newer)
    string? UpdateUrl);       // UpdateChecker: download / releases URL

/// <summary>The monitor's verdict. Message is null when nothing should show (healthy / benign).</summary>
public readonly record struct HealthVerdict(HealthState State, string? Message);

/// <summary>
/// Pure, clock-injected health state machine answering "is POE2GPS reading the game, or did a patch break
/// the offsets?". Fed one <see cref="ChainProbe"/> per world tick via <see cref="Evaluate"/>; holds every
/// latch/timer internally so it is fully unit-testable with a fake clock (same idiom as HoldRepeat /
/// ObjectiveClassifier). Read-only: it only interprets observations, never touches game memory or input.
/// </summary>
public sealed class OffsetHealthMonitor
{
    private static readonly TimeSpan PatternBrokeHint = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _holdOff;        // continuous "in zone, no player" before declaring Broken
    private readonly TimeSpan _postOkOffline;  // continuous NotInGame after an Ok before a soft warning
    private readonly TimeSpan _searchHint;     // continuous Searching before the soft update hint
    private readonly int _s4StableTicks;       // consecutive InZone ticks before "in a zone" is trusted
    private readonly int _radarEmptyTicks;     // consecutive Ok-but-no-terrain ticks before the soft warning

    private bool _everResolved;                // an Ok has occurred this session (the latch)
    private int _s4Stable;                     // consecutive ticks Stage >= InZone
    private int _okEmptyTicks;                 // consecutive Ok ticks with no terrain
    private DateTime? _loadingSince;           // start of the current continuous in-zone-no-player window
    private DateTime? _notInGameSince;         // start of the current continuous NotInGame window
    private DateTime? _searchingSince;         // start of the current continuous Searching window

    public OffsetHealthMonitor(TimeSpan holdOff, TimeSpan postOkOffline, TimeSpan searchHint,
                               int s4StableTicks, int radarEmptyTicks)
    {
        _holdOff = holdOff;
        _postOkOffline = postOkOffline;
        _searchHint = searchHint;
        _s4StableTicks = s4StableTicks;
        _radarEmptyTicks = radarEmptyTicks;
    }

    /// <summary>The shipping configuration: 25 s hold-off, 5 min post-Ok offline, 90 s search hint,
    /// 3-tick S4 stability, 10-tick radar-empty (≈330 ms at the 30 Hz world loop).</summary>
    public static OffsetHealthMonitor CreateDefault() =>
        new(TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90), 3, 10);

    public HealthVerdict Evaluate(ChainProbe p, DateTime now)
    {
        if (!p.Attached)
        {
            _s4Stable = 0; _okEmptyTicks = 0;
            _loadingSince = null; _notInGameSince = null; _searchingSince = null;
            return new HealthVerdict(HealthState.Waiting, "Path of Exile 2 is not running.");
        }

        if (p.SlotResolved)
        {
            // S4 stability: only trust "in a zone" after N consecutive InZone(+) ticks (filters ghost reads).
            var inZone = p.Stage >= ResolveStage.InZone;
            _s4Stable = inZone ? _s4Stable + 1 : 0;

            if (p.Stage == ResolveStage.Full)
            {
                _everResolved = true;
                _searchingSince = null; _notInGameSince = null; _loadingSince = null;
                _okEmptyTicks = p.TerrainPresent ? 0 : _okEmptyTicks + 1;
                return _okEmptyTicks >= _radarEmptyTicks
                    ? new HealthVerdict(HealthState.Ok,
                        "Reading your character but no map data — deep reads may be stale after a patch.")
                    : new HealthVerdict(HealthState.Ok, null);
            }

            _okEmptyTicks = 0;

            if (inZone && _s4Stable >= _s4StableTicks)
            {
                _searchingSince = null; _notInGameSince = null;
                _loadingSince ??= now;
                return now - _loadingSince >= _holdOff
                    ? new HealthVerdict(HealthState.Broken, BrokenMessage(p))
                    : new HealthVerdict(HealthState.Loading, null);
            }

            _loadingSince = null;

            if (_everResolved)
            {
                _searchingSince = null;
                _notInGameSince ??= now;
                return now - _notInGameSince >= _postOkOffline
                    ? new HealthVerdict(HealthState.NotInGame, OfflineMessage(p))
                    : new HealthVerdict(HealthState.NotInGame, null);
            }

            // Slot resolved but never reached Full this session and not currently in a stable zone.
            _searchingSince ??= now;
            return new HealthVerdict(HealthState.Searching, SearchingMessage(p, now));
        }

        // No in-zone-validated slot yet this attach.
        _notInGameSince = null; _loadingSince = null;
        _searchingSince ??= now;
        return new HealthVerdict(HealthState.Searching, SearchingMessage(p, now));
    }

    private static string BrokenMessage(ChainProbe p) =>
        p.UpdateAvailable ? $"Update available — download: {p.UpdateUrl}"
        : p.UpdateChecked ? "POE2GPS is up to date but can't read the game — try restarting in a loaded zone."
        : "POE2GPS can't read Path of Exile 2 — it likely just updated; a fix is coming.";

    private static string OfflineMessage(ChainProbe p) =>
        p.UpdateAvailable ? $"Radar's been offline a while. Update available — download: {p.UpdateUrl}"
        : "Radar's been offline a while — if you're in a zone, a patch may have shifted offsets. Check for a new release.";

    private string SearchingMessage(ChainProbe p, DateTime now)
    {
        var age = _searchingSince is { } s ? now - s : TimeSpan.Zero;
        var patternBroke = p.AobScanned && p.AobCandidateCount == 0;
        if ((patternBroke && age >= PatternBrokeHint) || age >= _searchHint)
        {
            if (p.UpdateAvailable) return $"Update available — download: {p.UpdateUrl}";
            return patternBroke
                ? "POE2GPS can't find Path of Exile 2's game state — it likely just updated. Check for a new release."
                : "Still can't read the game — if you're in a zone, POE2GPS may need an update. Check for a new release.";
        }
        return "Connecting to Path of Exile 2 — load into a zone.";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter OffsetHealthMonitorTests`
Expected: PASS (14/14).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Health/OffsetHealthMonitor.cs tests/POE2Radar.Tests/OffsetHealthMonitorTests.cs
git commit -m "feat(health): pure OffsetHealthMonitor state machine + tests"
```

---

### Task 2: Core — `Poe2Live.Probe()` + slot rebind

**Files:**
- Modify: `src/POE2Radar.Core/Game/Poe2Live.cs` (field `_gameStateSlot` ~16; ctor ~50; `TryResolve` ~116-141; add usings + new methods)

**Interfaces:**
- Consumes: `ResolveStage` (Task 1).
- Produces: `ResolveStage Poe2Live.Probe(out nint inGameState, out nint areaInstance, out nint localPlayer, out uint areaHash, out int areaLevel)`; `nint Poe2Live.Slot { get; }`; `void Poe2Live.Rebind(nint gameStateSlot)`. `TryResolve` keeps its existing signature/behavior (now delegates to `Probe`).

- [ ] **Step 1: Add the `Health` using**

At the top of `src/POE2Radar.Core/Game/Poe2Live.cs`, after the existing namespace/usings, add:

```csharp
using POE2Radar.Core.Health;
```

(The file starts with `namespace POE2Radar.Core.Game;` and no using block — add the using line above that namespace declaration.)

- [ ] **Step 2: Make the slot rebindable**

Change line ~16 from:

```csharp
    private readonly nint _gameStateSlot;
```

to:

```csharp
    private nint _gameStateSlot;   // (was readonly) — rebindable for lazy/late resolution + re-attach
```

- [ ] **Step 3: Add `Slot` + `Rebind`**

Immediately after the constructor (the `public Poe2Live(MemoryReader reader, nint gameStateSlot) { ... }` block ending ~line 54), add:

```csharp
    /// <summary>The GameState slot this reader is currently bound to (0 = not yet resolved).</summary>
    public nint Slot => _gameStateSlot;

    /// <summary>Late-bind (or re-bind, on re-attach) the GameState slot. Clears every per-entity/per-area
    /// cache, whose keys (entity / AreaInstance addresses) are meaningless under a new slot or process.
    /// Call only from the thread that owns this Poe2Live instance.</summary>
    public void Rebind(nint gameStateSlot)
    {
        _gameStateSlot = gameStateSlot;
        _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear();
        _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _mods.Clear();
        _itemIdent.Clear(); _idAt.Clear();
        _entCacheKey = 0;
        _league = ""; _leagueFor = -1;
        _areaCode = ""; _areaCodeFor = -1;
        _plPlayer = 0; _plPlayerFor = 0;
    }
```

> Implementer note: clear **every** address-keyed cache field declared in this file. The list above covers the Dictionaries + the `*For` sentinel fields visible near the top + `LeagueName`/`AreaCode`/`PlayerComp` caches. If the file declares additional `_xxxFor` / cached-component sentinel fields (e.g. a Life-component cache used by `PlayerVitals`), reset those too. First-resolve (slot 0 → real) starts from empty caches, so this matters mainly for re-attach (Task 7); the reviewer verifies completeness.

- [ ] **Step 4: Replace `TryResolve` with `Probe` + a delegating `TryResolve`**

Replace the whole `TryResolve` method (~lines 115-141) with:

```csharp
    /// <summary>Graduated resolve: report how far the GameState → InGameState → AreaInstance → LocalPlayer
    /// chain got this tick, plus the patch-stable low fields, so the health monitor can tell "in a zone but
    /// can't read the player" (offsets broke) from "at a menu / loading". Returns the resolved handles when
    /// it reaches <see cref="ResolveStage.Full"/>; <paramref name="areaHash"/>/<paramref name="areaLevel"/>
    /// are valid whenever the stage is <see cref="ResolveStage.InZone"/> or <see cref="ResolveStage.Full"/>.</summary>
    public ResolveStage Probe(out nint inGameState, out nint areaInstance, out nint localPlayer,
                              out uint areaHash, out int areaLevel)
    {
        inGameState = areaInstance = localPlayer = 0; areaHash = 0; areaLevel = 0;
        var gameState = Ptr(_gameStateSlot);
        if (gameState == 0) return ResolveStage.None;

        var best = ResolveStage.GameState;
        var candidates = new List<nint>(13);
        var vecFirst = Ptr(gameState + Poe2.GameState.CurrentStatePtr);
        if (vecFirst != 0) candidates.Add(Ptr(vecFirst));
        for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            candidates.Add(Ptr(gameState + Poe2.GameState.States + (nint)(i * Poe2.GameState.StateSlotStride)));

        foreach (var igs in candidates)
        {
            if (igs == 0) continue;
            if (best < ResolveStage.InGameState) best = ResolveStage.InGameState;
            var ai = Ptr(igs + Poe2.InGameState.AreaInstanceData);
            if (ai == 0) continue;

            // S4 low fields: direct scalar reads (no sub-pointer) — patch-stable, valid early in zone load.
            _reader.TryReadStruct<uint>(ai + Poe2.AreaInstance.CurrentAreaHash, out var h);
            _reader.TryReadStruct<int>(ai + Poe2.AreaInstance.CurrentAreaLevel, out var lvl);
            var thisInZone = h != 0 && lvl >= 0 && lvl <= 100;

            var lp = Ptr(ai + Poe2.AreaInstance.LocalPlayer);
            if (lp != 0 && ReadMetadata(lp).StartsWith("Metadata/", StringComparison.Ordinal))
            {
                inGameState = igs; areaInstance = ai; localPlayer = lp; areaHash = h; areaLevel = lvl;
                return ResolveStage.Full;   // best possible — take the first fully-valid candidate
            }
            if (thisInZone)
            {
                if (best < ResolveStage.InZone)
                {
                    best = ResolveStage.InZone;
                    inGameState = igs; areaInstance = ai; areaHash = h; areaLevel = lvl;
                }
            }
            else if (best < ResolveStage.AreaInstance)
            {
                best = ResolveStage.AreaInstance;
                inGameState = igs; areaInstance = ai;
            }
        }
        return best;
    }

    /// <summary>Resolve the in-game chain. Returns false during loading / character select / a broken patch.
    /// (Now a thin wrapper over <see cref="Probe"/> — true iff the chain fully resolved.)</summary>
    public bool TryResolve(out nint inGameState, out nint areaInstance, out nint localPlayer)
    {
        var stage = Probe(out inGameState, out areaInstance, out localPlayer, out _, out _);
        if (stage == ResolveStage.Full) return true;
        inGameState = areaInstance = localPlayer = 0;
        return false;
    }
```

- [ ] **Step 5: Build and run the full suite (no behavior regression)**

Run: `dotnet build POE2Radar.slnx -c Debug` then `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`
Expected: build succeeds; all existing tests still PASS (every `TryResolve` caller is unchanged — `TryResolve` is now `Probe == Full`). `Probe` itself is exercised in-game (manual smoke), matching the existing un-unit-tested `Poe2Live` pattern.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "feat(health): Poe2Live.Probe graduated resolve + rebindable slot"
```

---

### Task 3: Overlay — lazy slot resolver (non-fatal, self-connecting startup)

**Files:**
- Modify: `src/POE2Radar.Overlay/Bootstrap.cs` (turn into a reusable scan helper)
- Modify: `src/POE2Radar.Overlay/Program.cs` (drop the blocking resolve + exit)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (ctor drops the slot; build readers with slot 0; resolver thread; rebind on each consumer thread)

**Interfaces:**
- Consumes: `Poe2Live.Probe`/`Rebind`/`Slot` (Task 2); `ResolveStage` (Task 1); `AobScanner.ScanForResolvedAddresses(ProcessHandle, MemoryReader, AobPatterns.Pattern) → List<nint>`; `AobPatterns.GameStateRefs`.
- Produces: `RadarApp(ProcessHandle process, MemoryReader reader)` ctor (no slot); `Bootstrap.ScanForSlot(ProcessHandle, MemoryReader, out int candidateCount) → nint`; volatile fields on `RadarApp`: `_resolvedSlot` (nint), `_attached` (bool), `_slotResolved` (bool), `_aobScanned` (bool), `_aobCandidates` (int).

- [ ] **Step 1: Rewrite `Bootstrap` as a reusable scan helper**

Replace the whole body of `src/POE2Radar.Overlay/Bootstrap.cs` with:

```csharp
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Core.Health;

namespace POE2Radar.Overlay;

/// <summary>
/// Scans for the PoE2 GameState global-pointer slot via the "Game States" AOB pattern, accepting the slot
/// whose chain resolves at least to a real in-zone AreaInstance (the patch-stable low fields). Stateless +
/// re-runnable — the lazy SlotResolver in RadarApp calls it on a cadence until it returns non-zero.
/// </summary>
internal static class Bootstrap
{
    /// <summary>Scan + validate. Returns the best slot whose chain reaches at least
    /// <see cref="ResolveStage.InZone"/> (preferring <see cref="ResolveStage.Full"/>), else 0. Sets
    /// <paramref name="candidateCount"/> to the raw number of AOB hits (0 = the pattern matched nothing).</summary>
    public static nint ScanForSlot(ProcessHandle process, MemoryReader reader, out int candidateCount)
    {
        candidateCount = 0;
        nint bestSlot = 0;
        var bestStage = ResolveStage.None;
        var probe = new Poe2Live(reader, 0);

        foreach (var pattern in AobPatterns.GameStateRefs)
        {
            foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern).Distinct())
            {
                candidateCount++;
                probe.Rebind(slot);
                var stage = probe.Probe(out _, out _, out _, out _, out _);
                if (stage > bestStage) { bestStage = stage; bestSlot = slot; }
                if (stage == ResolveStage.Full) return slot;   // best possible — stop early
            }
        }
        return bestStage >= ResolveStage.InZone ? bestSlot : 0;
    }
}
```

- [ ] **Step 2: Make `Program.cs` non-fatal**

In `src/POE2Radar.Overlay/Program.cs`, replace lines 44-52 (from `var reader = new MemoryReader(process);` through `using var app = new RadarApp(process, reader, slot);`) with:

```csharp
var reader = new MemoryReader(process);

Console.WriteLine();
POE2Radar.Overlay.Overlay.ConsoleTheme.Accent("Running. The overlay connects automatically once you're in a zone. Ctrl+C to exit.");

using var app = new RadarApp(process, reader);
```

(Delete the `var slot = Bootstrap.ResolveGameStateSlot(...)` call and the `if (slot == 0) return 2;` guard. The `ProcessHandle.AttachToPoE()` null-check above it — "Game not running" / `return 1` — stays: a process is still required to start.)

- [ ] **Step 3: Add resolver fields to `RadarApp`**

In `src/POE2Radar.Overlay/RadarApp.cs`, next to the existing thread/shutdown fields (near `_shutdown` ~line 189), add:

```csharp
    // ── Lazy GameState-slot resolver (Task: patch-resilience). The overlay starts before the slot is
    // known; this background thread scans until a chain validates, then publishes the slot for the three
    // reader threads to rebind. Volatile so the world/render/API threads + monitor see fresh values. ──
    private Thread? _resolverThread;
    private volatile nint _resolvedSlot;     // 0 until an in-zone slot is validated this attach
    private volatile bool _attached = true;  // PoE2 process is alive
    private volatile bool _slotResolved;     // an in-zone slot has been published this attach
    private volatile bool _aobScanned;       // the resolver completed at least one scan
    private volatile int  _aobCandidates;    // candidate count from the last scan (0 = pattern matched nothing)
```

- [ ] **Step 4: Build the readers with slot 0 + drop the ctor slot param**

Change the ctor signature (line 261) and the three reader constructions (lines 273-280). From:

```csharp
    public RadarApp(ProcessHandle process, MemoryReader reader, nint gameStateSlot)
    {
```

to:

```csharp
    public RadarApp(ProcessHandle process, MemoryReader reader)
    {
```

and change lines 273 / 278 / 280 from `new Poe2Live(reader, gameStateSlot)` / `new Poe2Live(_readerRender, gameStateSlot)` / `new Poe2Live(_readerApi, gameStateSlot)` to pass `0`:

```csharp
        _live = new Poe2Live(reader, 0);
```
```csharp
        _liveRender = new Poe2Live(_readerRender, 0);
```
```csharp
        _liveApi = new Poe2Live(_readerApi, 0);
```

(The `_live.CustomLandmarkMatch` / `CuratedLookup` / `LandmarkClusterGap` assignments stay where they are — they don't need the slot.)

- [ ] **Step 5: Start the resolver thread in `Run()` and add the resolver loop**

In `Run()` (line 598), immediately after `_worldThread.Start();` (line 605), add:

```csharp
        _resolverThread = new Thread(ResolverLoop) { IsBackground = true, Name = "POE2Radar.Resolver" };
        _resolverThread.Start();
```

Then add the resolver method (place it next to `WorldLoop`, ~line 662). Note `using POE2Radar.Core.Health;` may be needed at the top of the file if not already present:

```csharp
    /// <summary>Background slot resolver (1.5 s cadence): on its OWN reader stack, scan for the GameState
    /// slot until a chain validates in a zone, then publish it for the consumer threads to rebind. Tracks
    /// attach state + AOB candidate count for the health monitor. Re-attach (game restart) lands in a later
    /// task; for now a dead process just flips _attached = false.</summary>
    private void ResolverLoop()
    {
        var resolverReader = new MemoryReader(_process);   // isolated reader — never shares buffers with a thread
        while (!_shutdown)
        {
            bool alive;
            try { using var p = System.Diagnostics.Process.GetProcessById(_process.ProcessId); alive = !p.HasExited; }
            catch { alive = false; }
            _attached = alive;

            if (alive && _resolvedSlot == 0)
            {
                var slot = Bootstrap.ScanForSlot(_process, resolverReader, out var candidates);
                _aobCandidates = candidates;
                _aobScanned = true;
                if (slot != 0)
                {
                    _resolvedSlot = slot;
                    _slotResolved = true;
                    ConsoleTheme.Ok($"GameState slot resolved: 0x{slot:X16}");
                }
                else
                {
                    ConsoleTheme.WarnLine(candidates == 0
                        ? "  Waiting — game-state pattern not found yet (load into a zone; if this persists POE2GPS may need an update)."
                        : "  Waiting for in-game state — load into a zone.");
                }
            }
            Thread.Sleep(1500);
        }
    }
```

- [ ] **Step 6: Rebind on each consumer thread before it reads**

In `WorldLoop` (line 670, inside the `try`, immediately before `if (_live.TryResolve(...`), add:

```csharp
                if (_live.Slot != _resolvedSlot) _live.Rebind(_resolvedSlot);
```

In `Tick` (render thread, ~line 803, immediately before `var inGame = _liveRender.TryResolve(...`), add:

```csharp
        if (_liveRender.Slot != _resolvedSlot) _liveRender.Rebind(_resolvedSlot);
```

For the API reader: in every method that reads via `_liveApi` (search the file for `_liveApi.` — e.g. `CurrentTilePaths` and any atlas/tile API read), add at the start of the method:

```csharp
        if (_liveApi.Slot != _resolvedSlot) _liveApi.Rebind(_resolvedSlot);
```

- [ ] **Step 7: Join the resolver thread on Dispose**

In `Dispose()` (line 2446), after `_worldThread?.Join(1000);`, add:

```csharp
        _resolverThread?.Join(1000);
```

- [ ] **Step 8: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: succeeds. (Behavioral check is manual: launch the overlay at the login screen → it stays up and connects when you zone in, instead of exiting.)

- [ ] **Step 9: Commit**

```bash
git add src/POE2Radar.Overlay/Bootstrap.cs src/POE2Radar.Overlay/Program.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(health): lazy slot resolver — non-fatal, self-connecting startup"
```

---

### Task 4: Overlay — wire the monitor + publish health to `/state`

**Files:**
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (monitor field; replace the `WorldLoop` resolve; `EvaluateHealth`; publish fields; add to `_state`)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`RadarState` record; `/state` JSON)

**Interfaces:**
- Consumes: `OffsetHealthMonitor`, `ChainProbe`, `HealthState`, `ResolveStage` (Task 1); `_resolvedSlot`/`_attached`/`_slotResolved`/`_aobScanned`/`_aobCandidates` (Task 3); `_update` (`UpdateChecker.Result?`, existing).
- Produces: volatile `RadarApp._healthState` (HealthState) + `_healthMessage` (string?); `RadarState.Health` (HealthState, default `Searching`) + `RadarState.HealthMessage` (string?); `/state` fields `healthState` (lowercase string) + `healthMessage`.

- [ ] **Step 1: Add monitor + published health fields to `RadarApp`**

Near the resolver fields added in Task 3 (~line 189), add:

```csharp
    private readonly POE2Radar.Core.Health.OffsetHealthMonitor _health =
        POE2Radar.Core.Health.OffsetHealthMonitor.CreateDefault();
    private volatile POE2Radar.Core.Health.HealthState _healthState = POE2Radar.Core.Health.HealthState.Searching;
    private volatile string? _healthMessage;
```

- [ ] **Step 2: Probe + evaluate health in `WorldLoop`**

Replace the `try { ... }` body of `WorldLoop` (lines 668-674) — the block:

```csharp
            try
            {
                if (_live.TryResolve(out var inGameState, out var areaInstance, out var localPlayer))
                    WorldTick(inGameState, areaInstance, localPlayer);
                else
                    PublishEmptyWorld();
            }
```

with:

```csharp
            try
            {
                if (_live.Slot != _resolvedSlot) _live.Rebind(_resolvedSlot);
                var stage = _live.Probe(out var inGameState, out var areaInstance, out var localPlayer, out _, out _);
                if (stage == POE2Radar.Core.Health.ResolveStage.Full)
                    WorldTick(inGameState, areaInstance, localPlayer);
                else
                    PublishEmptyWorld();
                EvaluateHealth(stage);
            }
```

(This replaces the `_live.Slot` rebind line added in Task 3 Step 6 — it now lives inside this block. Remove the standalone rebind line if it duplicates.)

- [ ] **Step 3: Add `EvaluateHealth`**

Add next to `WorldLoop`:

```csharp
    /// <summary>Build the health observation for this world tick and publish the monitor's verdict. World
    /// thread only (reads _terrain, written by WorldTick). _terrain != null only at Full, which is exactly
    /// when TerrainPresent matters (the radar-empty soft warning).</summary>
    private void EvaluateHealth(POE2Radar.Core.Health.ResolveStage stage)
    {
        var u = _update;
        var probe = new POE2Radar.Core.Health.ChainProbe(
            Attached:           _attached,
            SlotResolved:       _slotResolved,
            AobCandidateCount:  _aobCandidates,
            AobScanned:         _aobScanned,
            Stage:              stage,
            TerrainPresent:     _terrain != null,
            UpdateAvailable:    u?.UpdateAvailable ?? false,
            UpdateChecked:      u?.Latest != null,
            UpdateUrl:          u?.Url ?? UpdateChecker.ReleasesPage);
        var v = _health.Evaluate(probe, DateTime.UtcNow);
        _healthState = v.State;
        _healthMessage = v.Message;
    }
```

- [ ] **Step 4: Publish health in `_state`**

In `Tick`, change the `_state = new RadarState(...)` tail (line 931) from:

```csharp
            snap.AreaCode, "", snap.CharLevel, _worldMs, _renderMs, mr.Markers, _directorQueue, _fps,
            Session: _sessionSnapshot);
```

to:

```csharp
            snap.AreaCode, "", snap.CharLevel, _worldMs, _renderMs, mr.Markers, _directorQueue, _fps,
            Session: _sessionSnapshot, Health: _healthState, HealthMessage: _healthMessage);
```

- [ ] **Step 5: Add the fields to `RadarState` + `/state`**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`, add at the top with the other usings:

```csharp
using POE2Radar.Core.Health;
```

Change the `RadarState` record tail (lines 1280-1281) from:

```csharp
    // Session HUD data: elapsed times, pace, zone context, deaths. Null when tracker not running.
    SessionStats? Session = null)
```

to:

```csharp
    // Session HUD data: elapsed times, pace, zone context, deaths. Null when tracker not running.
    SessionStats? Session = null,
    // Patch-resilience health: State drives the dashboard Status ticks; Message is the banner/Status text
    // (null when healthy / benign). Optional + trailing so RadarState.Empty (positional) is unaffected.
    HealthState Health = HealthState.Searching,
    string? HealthMessage = null)
```

In the `/state` handler, add two lines to the anonymous object (after `worldMs = s.WorldMs, renderMs = s.RenderMs, fps = s.Fps,` on line 185):

```csharp
                    healthState = s.Health.ToString().ToLowerInvariant(),
                    healthMessage = s.HealthMessage,
```

- [ ] **Step 6: Build + verify the suite + scrub self-test**

Run: `dotnet build POE2Radar.slnx -c Debug` then `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj` then `pwsh scripts/scrub-strings.ps1 -SelfTest` and `pwsh scripts/compliance-gate.ps1`
Expected: build + tests pass; compliance + scrub self-test green. (Manual: `curl http://localhost:7777/state` shows `healthState` transitioning `searching`→`ok`.)

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(health): evaluate monitor each world tick; expose health on /state"
```

---

### Task 5: Overlay — health banner (`OverlayRenderer` + `RenderContext`)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (two trailing params)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (pass them into the `RenderContext` ctor)
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (`DrawHealthBanner` + call site)

**Interfaces:**
- Consumes: `RadarApp._healthState`/`_healthMessage` (Task 4); `HealthState` (Task 1).
- Produces: `RenderContext.Health` (HealthState, default `Ok`) + `RenderContext.HealthMessage` (string?).

- [ ] **Step 1: Add the two trailing params to `RenderContext`**

In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, change the final parameter (line 199) from:

```csharp
    Config.SessionHudSettings             SessionHudSettings = null!);
```

to:

```csharp
    Config.SessionHudSettings             SessionHudSettings = null!,
    // ── Patch-resilience health banner. Health picks the banner color; HealthMessage is the text
    // (null → no banner). Drawn whenever the overlay is Active and HealthMessage != null. ──
    POE2Radar.Core.Health.HealthState     Health             = POE2Radar.Core.Health.HealthState.Ok,
    string?                               HealthMessage      = null);
```

- [ ] **Step 2: Pass them at the `RenderContext` construction**

In `RadarApp.Tick`, change the final `RenderContext` argument (line 1009) from:

```csharp
            Session: _sessionSnapshot,
            SessionHudSettings: _settings.SessionHud);
```

to:

```csharp
            Session: _sessionSnapshot,
            SessionHudSettings: _settings.SessionHud,
            Health: _healthState,
            HealthMessage: _healthMessage);
```

- [ ] **Step 3: Add the banner draw call**

In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`, add at the top with the usings:

```csharp
using POE2Radar.Core.Health;
```

In `Render`, inside the `try` block, immediately before the closing `}` that precedes `finally { rt.EndDraw(); }` (i.e. after the `if (ctx.Active && ctx.InGame) { DrawRuneforge... DrawSessionHud(rt, ctx); }` block, ~line 135), add:

```csharp
            // Patch-resilience banner: top strip drawn on top of everything whenever the overlay is active
            // and the health monitor has something to say (connecting / out-of-date / stale reads).
            if (ctx.Active && ctx.HealthMessage != null)
                DrawHealthBanner(rt, ctx);
```

- [ ] **Step 4: Add `DrawHealthBanner`**

Add the method after `DrawCycleIndicator` (~line 355), mirroring `DrawSessionHud`'s `FillRectangle` (RawRectF) + `DrawText` (Rect) pattern:

```csharp
    /// <summary>Top-strip status/health banner — drawn whenever the overlay is active and the health monitor
    /// has a message. Red for a confirmed can't-read-the-game; amber for connecting / soft warnings.</summary>
    private void DrawHealthBanner(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.HealthMessage is not { Length: > 0 } msg) return;
        rt.FillRectangle(new Vortice.RawRectF(0f, 0f, ctx.WindowWidth, 30f), _bPanel!);
        _bStyle!.Color = ctx.Health == HealthState.Broken
            ? new Color4(1f, 0.20f, 0.20f, 0.95f)   // red — confirmed break
            : new Color4(1f, 0.85f, 0.20f, 1f);     // amber — connecting / soft warning
        rt.DrawText("⚠ " + msg, _tf!, new Rect(12f, 7f, ctx.WindowWidth - 12f, 30f), _bStyle!, DrawTextOptions.Clip);
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: succeeds. (Manual: with PoE2 focused at the login screen the overlay shows an amber "Connecting…" strip; on a broken patch, a red out-of-date strip.)

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(health): overlay top-strip health banner"
```

---

### Task 6: Dashboard — Status panel

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (Status card markup + `renderState()` JS)

**Interfaces:**
- Consumes: `/state` `healthState` + `healthMessage` + `inGame` (Task 4).

- [ ] **Step 1: Add the Status card as the first Settings card**

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, find the Settings panel-grid + first card (lines 539-540):

```csharp
        <div class="panel-grid">
          <div class="card">
            <h3>Target cycling</h3>
```

Insert a Status card before the Target-cycling card so it reads:

```csharp
        <div class="panel-grid">
          <div class="card" id="statusCard" style="grid-column:1/-1">
            <h3>Status</h3>
            <div class="row"><div class="rl">Attached to Path of Exile 2</div><div class="ro" id="stAttach">&mdash;</div></div>
            <div class="row"><div class="rl">In a zone</div><div class="ro" id="stZone">&mdash;</div></div>
            <div class="row"><div class="rl">Reading your character</div><div class="ro" id="stPlayer">&mdash;</div></div>
            <div class="row" id="stMsgRow" hidden><div class="rl" id="stMsg" style="font-style:italic"></div></div>
          </div>
          <div class="card">
            <h3>Target cycling</h3>
```

- [ ] **Step 2: Render the Status card each tick**

In the `renderState()` function (`src/POE2Radar.Overlay/Web/DashboardHtml.cs` ~line 1730), immediately after `const s=state; if(!s) return;`, add:

```javascript
  // Patch-resilience Status panel: derive the three ticks from healthState (see OffsetHealthMonitor).
  const hsName = s.healthState || 'searching';
  const stTick = ok => ok
    ? '<span style="color:var(--good)">&#10003;</span>'
    : '<span style="color:var(--ink-faint)">&#9675;</span>';
  $('#stAttach').innerHTML = stTick(hsName !== 'waiting');
  $('#stZone').innerHTML   = stTick(hsName === 'ok' || hsName === 'loading');
  $('#stPlayer').innerHTML = stTick(hsName === 'ok');
  const stRow = $('#stMsgRow'), stMsg = $('#stMsg');
  if (s.healthMessage) {
    stRow.hidden = false;
    stMsg.textContent = '⚠ ' + s.healthMessage;
    stMsg.style.color = (hsName === 'broken') ? 'var(--blood)' : 'var(--gold)';
  } else { stRow.hidden = true; stMsg.textContent = ''; }
```

- [ ] **Step 3: Build**

Run: `dotnet build POE2Radar.slnx -c Debug`
Expected: succeeds (the dashboard is a compiled string; a build catches gross C#-string breakage). Manual: open the dashboard (F12) — the Status card shows Attached ✓ / In a zone / Reading your character, with the message line when degraded.

- [ ] **Step 4: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(health): dashboard Status panel"
```

---

### Task 7: Overlay — re-attach on game restart (final, cuttable)

**Files:**
- Modify: `src/POE2Radar.Core/ProcessHandle.cs` (`TryReattach` + make `ProcessId`/`MainModuleBase`/`MainModuleSize` settable)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`ResolverLoop` re-attach branch)

**Interfaces:**
- Consumes: existing `ProcessHandle.AttachToProcess`, `NativeMethods.CloseHandle`.
- Produces: `bool ProcessHandle.TryReattach(IReadOnlyList<string>? candidateNames = null)`.

- [ ] **Step 1: Make the swappable properties settable**

In `src/POE2Radar.Core/ProcessHandle.cs`, change lines 14, 17, 18 from `public int ProcessId { get; }` / `public nint MainModuleBase { get; }` / `public uint MainModuleSize { get; }` to `private set`:

```csharp
    public int ProcessId { get; private set; }
```
```csharp
    public nint MainModuleBase { get; private set; }
    public uint MainModuleSize { get; private set; }
```

- [ ] **Step 2: Add `TryReattach`**

Add after `AttachToProcess` (~line 86):

```csharp
    /// <summary>Re-open a freshly-launched PoE2 client in place (after the previous one exited), refreshing
    /// this handle + module base/size so the existing MemoryReaders keep working against the new process.
    /// Returns true if a client was found and opened. Read-only access mask, same as AttachToProcess.
    /// Called only from the slot resolver thread, and only after the old process has died (so no read is in
    /// flight against the handle being swapped).</summary>
    public bool TryReattach(IReadOnlyList<string>? candidateNames = null)
    {
        candidateNames ??= ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExile_KG", "PathOfExileEGS"];
        foreach (var name in candidateNames)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                if (procs.Length == 0) continue;
                var fresh = AttachToProcess(procs[0].Id, name);
                var old = Handle;
                Handle = fresh.Handle;
                MainModuleBase = fresh.MainModuleBase;
                MainModuleSize = fresh.MainModuleSize;
                ProcessId = fresh.ProcessId;
                ProcessName = fresh.ProcessName;
                fresh.Handle = 0;   // we took ownership of the handle — stop fresh from closing it
                if (old != 0) NativeMethods.CloseHandle(old);
                return true;
            }
            catch { /* try the next candidate name */ }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }
```

> `ProcessName` is currently `{ get; }` (line 15) — change it to `{ get; private set; }` as well so `TryReattach` can update it.

- [ ] **Step 3: Re-attach in the resolver loop**

In `RadarApp.ResolverLoop` (Task 3), replace the liveness block:

```csharp
            bool alive;
            try { using var p = System.Diagnostics.Process.GetProcessById(_process.ProcessId); alive = !p.HasExited; }
            catch { alive = false; }
            _attached = alive;
```

with:

```csharp
            bool alive;
            try { using var p = System.Diagnostics.Process.GetProcessById(_process.ProcessId); alive = !p.HasExited; }
            catch { alive = false; }

            if (!alive)
            {
                // Game closed/restarted: try to re-attach to a fresh client and re-resolve from scratch.
                if (_process.TryReattach())
                {
                    alive = true;
                    _resolvedSlot = 0; _slotResolved = false; _aobScanned = false;
                    ConsoleTheme.Accent("Re-attached to a new Path of Exile 2 client — re-resolving…");
                }
            }
            _attached = alive;
```

- [ ] **Step 4: Build + full CI**

Run: `dotnet build POE2Radar.slnx -c Debug` then `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj` then `pwsh scripts/compliance-gate.ps1`
Expected: all green. (Manual: close PoE2 with the overlay running → Status shows "Path of Exile 2 is not running"; relaunch PoE2 → the overlay re-attaches and reconnects when you zone in.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/ProcessHandle.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(health): re-attach to a restarted PoE2 client"
```

---

## Post-implementation (after the final whole-branch review)

- Bump `<Version>` in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` from `0.5.2` to `0.6.0` (minor feature) in the same final pass, then follow the release ritual (ff-merge to main, tag check, push, `release.yml`, `gh release edit --repo luther-rotmg/POE2GPS`, Discord announcement + soft Ko-fi plug).

## Self-Review

**Spec coverage:** Lazy-resolve always-up UI → Task 3. Graduated `AreaHash`-anchored S4 probe → Task 2. Six-state machine + hold-off + S4 stability + AOB-zero split + two soft escapes + update-aware messages → Task 1 (with tests). Process-death → `Waiting` → Task 3 (liveness) + Task 7 (re-attach). Overlay banner → Task 5. `/state` + `RadarState` → Task 4. Dashboard Status panel → Task 6. Compliance/scrub gates → Tasks 4 & 7 verification steps. Re-attach cuttable/last → Task 7. All spec sections map to a task.

**Placeholder scan:** No TBD/TODO; every code step shows complete code; thresholds are concrete (Task 1 ctor + `CreateDefault`).

**Type consistency:** `HealthState`/`ResolveStage`/`ChainProbe`/`HealthVerdict` defined in Task 1 and consumed verbatim in Tasks 2/3/4/5. `ChainProbe` field order (Attached, SlotResolved, AobCandidateCount, AobScanned, Stage, TerrainPresent, UpdateAvailable, UpdateChecked, UpdateUrl) matches the named-arg construction in Task 4 Step 3 and the test helper in Task 1. `Probe` out-param order (inGameState, areaInstance, localPlayer, areaHash, areaLevel) matches every call site (Tasks 2, 3, 4). `RadarState`/`RenderContext` both gain `Health` + `HealthMessage` as the last two optional params, passed by name at their construction sites.
