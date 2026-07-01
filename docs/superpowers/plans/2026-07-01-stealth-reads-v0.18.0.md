# Performance v3: Stealth Reads v0.18.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Cut `ReadProcessMemory` calls/sec (the `rpmPerSec` metric) as far as possible while retaining full user-visible functionality — smaller detection surface (stealth) + less CPU.

**Architecture:** Feature-gate reads to enabled features, dedupe redundant reads, cache static-per-zone data, slow-refresh imperceptible data, cull-before-read, and skip the render read-block when unfocused. All changes are internal to the read path (`Poe2Live`, `Poe2Atlas`, `RadarApp` tick orchestration); the published `WorldSnapshot`/`RadarState`/`AtlasRender` shapes are unchanged. Invisible-only: nothing the user sees or uses changes.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64). Read primitives are `_reader.TryReadStruct<T>` / `TryReadBytes` / the `Ptr()` helper.

## Global Constraints

- **Strictly read-only of the game.** This release *removes* reads; never adds writes/injection/input/pricing. No new offsets.
- **Invisible-only guardrail.** A reduction ships only if it produces NO user-visible change: cut invisible reads (feature-gated + culled) + imperceptible refresh (data changing slower than perception; ~30 Hz floor for anything animated on-screen). Never change a published snapshot/API shape.
- **No auto-flask exists in this fork** (it was removed for compliance) — `PlayerVitals` feeds only the HUD %-bars, so it may be slow-refreshed freely; there is no safety-critical full-rate consumer.
- Platform: `net10.0-windows`, x64, `Nullable` enable, `TreatWarningsAsErrors=true` — warning-clean.
- The existing **268 tests must stay green** (regression guard). Test project references Core only; these are read-path behavioral changes with no natural unit test, so each task is verified by **build (0 CS errors) + 268 tests green + a stated live `rpmPerSec` scenario** (measured in-game via `/state`).
- README badge stays `0.5.4`. App `<Version>` → `0.18.0` (final task only).
- **Build note:** a running overlay locks `Overlay.dll`/`POE2Radar.Core.dll` → MSB3026/MSB3027 are copy-lock errors, not code errors; success = 0 CS errors.
- Full detail + rationale per reduction: `docs/superpowers/specs/2026-07-01-perf-v3-audit-synthesis.md`.

---

### Task SR-2: Unfocused idle-slowdown — gate the render read-block on focus

**Files:** Modify `src/POE2Radar.Overlay/RadarApp.cs` (`Tick()`, the `if (inGame)` block ~1263 and the `realActive`/`_overlayHadContent` logic).

**Rationale:** The render thread runs ~all its reads at 60 Hz even when PoE2 is unfocused and the overlay draws nothing (~7,680 reads/sec wasted while tabbed out). `_overlayHadContent` already implements the "one clear-frame then skip draw" pattern (line 1517–1521); reuse it to also skip the *reads*.

**Interfaces:** Consumes existing `realActive` (`_gameHwnd != 0 && GetForegroundWindow() == _gameHwnd`, computed at line 1405 — but note the read-block at 1263 runs BEFORE 1405; you must compute the foreground check before the block). Produces no new API.

- [ ] **Step 1: Compute the focus gate before the read block.** In `Tick()`, before the `if (inGame)` at line 1263, add a local:
```csharp
        var renderActive = _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd;
```
(If `realActive` is already computed earlier in the method for another purpose, reuse it; grounding shows it's computed at 1405 which is AFTER the block — so add this earlier local and reuse it at 1405 as `var realActive = renderActive;` to avoid a duplicate `GetForegroundWindow()` call.)

- [ ] **Step 2: Gate the read block.** Change `if (inGame)` (line 1263) to:
```csharp
        if (inGame && (renderActive || _settings.AlwaysShowOverlay || _overlayHadContent))
```
This keeps: (a) full reads when PoE2 is focused; (b) full reads when AlwaysShowOverlay is on (dashboard calibration must still see live data); (c) exactly ONE more read+draw frame after focus loss (via `_overlayHadContent`), then skips all reads until refocus. The existing `else { ... clear frames ... }` at line 1357 handles the skipped-frame state (it already clears the `_*Frame` scratch lists). Confirm the `else` branch still runs when the guard is false (it does — the guard is the `if`).

- [ ] **Step 3: Build + tests.** `dotnet build src/POE2Radar.Overlay -c Debug` → 0 CS errors. `dotnet test tests/POE2Radar.Tests` → 268 green.

- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): skip render read-block while PoE2 unfocused (SR-2 idle-slowdown)"
```
**Live rpmPerSec scenario:** in a zone, focused → note `/state` rpmPerSec; alt-tab away → rpmPerSec should drop to ~world-thread baseline (render reads gone).

---

### Task SR-3: Feature-gate ReadMods on affix-nameplate / mod-filter usage

**Files:** Modify `src/POE2Radar.Core/Game/Poe2Live.cs` (add `EnableModReads`; gate at line 526). Modify `src/POE2Radar.Overlay/RadarApp.cs` (`WorldTick`, before `_live.Entities(...)`).

**Rationale:** `ReadMods` (line 526, budget-capped 16/pass) is consumed only by affix nameplates + display-rule mod filters. When both are off (default) its zone-entry read burst is pure waste.

**Interfaces:** Produces `Poe2Live.EnableModReads` (bool, default true for safety). Consumes `_settings.AffixNameplates.Enabled` and whether any display rule uses a mod filter.

- [ ] **Step 1: Add the gate field.** In `Poe2Live.cs`, near the `_mods`/`_modReadBudget` fields (~38–44):
```csharp
    /// <summary>When false, monster affix-mod reads are skipped entirely (no consumer needs them). Set by
    /// RadarApp each world tick from the affix-nameplate + mod-filter feature state. Default true = fail-safe.</summary>
    public bool EnableModReads { get; set; } = true;
```

- [ ] **Step 2: Gate the read.** At `Poe2Live.cs:526`, change:
```csharp
            var mods = cat == EntityCategory.Monster ? ReadMods(entity) : null;
```
to:
```csharp
            var mods = (EnableModReads && cat == EntityCategory.Monster) ? ReadMods(entity) : null;
```

- [ ] **Step 3: Drive it from RadarApp.** In `RadarApp.WorldTick()`, immediately before the `_live.Entities(areaInstance)` call (grounding: ~line 1567), add:
```csharp
        _live.EnableModReads = _settings.AffixNameplates.Enabled || _displayRules.AnyModFilter;
```
If `_displayRules` has no `AnyModFilter` member, use a linear check over the enabled rules for a mod-filter predicate (grep `DisplayRules` for the rule shape; a rule references a mod matcher). If no such concept exists, gate on `_settings.AffixNameplates.Enabled` alone and note it in the report.

- [ ] **Step 4: Build + tests** (0 errors, 268 green).

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): gate monster mod reads on affix-nameplate/mod-filter usage (SR-3)"
```
**Live scenario:** enter a zone with AffixNameplates OFF → zone-entry rpmPerSec lower than with it ON.

---

### Task SR-4: Feature-gate ReadItemIdentity on ground-items usage

**Files:** Modify `Poe2Live.cs` (add `EnableItemIdentityReads`; gate at 532–533). Modify `RadarApp.cs` (`WorldTick`).

**Rationale:** `ReadItemIdentity` (line 532–533, budget-capped 12/pass) feeds only `BuildItemLabels` (ground-item value overlay), which early-returns when `GroundItems.Enabled` is false. When off (default) all item-identity reads are waste.

**Interfaces:** Produces `Poe2Live.EnableItemIdentityReads` (bool, default true).

- [ ] **Step 1: Add the field** (near `_itemReadBudget`, ~47):
```csharp
    /// <summary>When false, dropped-item identity reads (art/name/rarity) are skipped — the ground-item
    /// label overlay is off. Set by RadarApp per world tick. Default true = fail-safe.</summary>
    public bool EnableItemIdentityReads { get; set; } = true;
```

- [ ] **Step 2: Gate the read.** At `Poe2Live.cs:532–533`, change:
```csharp
            if (cat == EntityCategory.Other && meta.Contains("WorldItem", StringComparison.Ordinal))
                (rarity, itemArt, itemIdentified, itemName) = ReadItemIdentity(entity);
```
to:
```csharp
            if (EnableItemIdentityReads && cat == EntityCategory.Other && meta.Contains("WorldItem", StringComparison.Ordinal))
                (rarity, itemArt, itemIdentified, itemName) = ReadItemIdentity(entity);
```
(When skipped, `rarity` stays `Rarity.NonMonster`, `itemArt`/`itemName` stay null — the item dot still draws; only the value label is suppressed, which matches GroundItems being off.)

- [ ] **Step 3: Drive from RadarApp.** In `WorldTick()` next to SR-3's line:
```csharp
        _live.EnableItemIdentityReads = _settings.GroundItems.Enabled;
```
(If any display rule applies to items and needs item rarity, OR that in; else GroundItems.Enabled alone. Verify no other consumer reads WorldItem `EntityDot.Rarity`/`ItemArt`/`ItemName` when GroundItems is off; note findings in the report.)

- [ ] **Step 4: Build + tests** (0 errors, 268 green).

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): gate dropped-item identity reads on GroundItems overlay (SR-4)"
```
**Live scenario:** zone with drops, GroundItems OFF → lower zone-entry rpmPerSec.

---

### Task SR-5: Render-thread redundant-read quick wins (batch)

**Files:** Modify `RadarApp.cs` (`Tick()` lines 1267–1273). Modify `Poe2Live.cs` (`Entities` HP condition line 523).

**Rationale:** Four independent invisible dedupes/gates on the render hot path.

- [ ] **Step 1: (a) AreaHash from snapshot.** At `RadarApp.cs:1267`, delete `_areaHash = _liveRender.AreaHash(areaInstance);` and set `_areaHash = snap.AreaHash;` instead (the snapshot captured at line 1257 already carries it; zone-load guard tolerates the ≤33 ms lag since loads take seconds).

- [ ] **Step 2: (b) Single player Render read.** At `RadarApp.cs:1269–1270`, `PlayerGrid` and `PlayerWorld` both read the same Render component. Replace with one read:
```csharp
            playerWorld = _liveRender.PlayerWorld(localPlayer);   // one Render read
            player = playerWorld is { } pw ? new NumVec2(pw.X / Poe2.WorldToGridRatio, pw.Y / Poe2.WorldToGridRatio) : NumVec2.Zero;
```
(Confirm `Poe2.WorldToGridRatio` is accessible from RadarApp; grounding shows `Poe2Live.Entities` uses `Poe2.WorldToGridRatio`. If not accessible, add a `Poe2Live.GridFromWorld(Vector3)` helper and call it.)

- [ ] **Step 3: (c) Gate CameraMatrix.** At `RadarApp.cs:1272`, wrap:
```csharp
            _cameraMatrix = (_settings.HpBarNormal || _settings.HpBarMagic || _settings.HpBarRare || _settings.HpBarUnique
                || _settings.AffixNameplates.Enabled || _settings.GroundItems.Enabled)
                ? _liveRender.CameraMatrix(inGameState) : null;
```
(The renderer already null-checks `_cameraMatrix` before world-space draws — nameplates/labels/HP bars — so null = skip, which is correct when none are on.)

- [ ] **Step 4: (d) Drop Player from ReadHp.** At `Poe2Live.cs:523`, change `if (cat is EntityCategory.Monster or EntityCategory.Player)` to `if (cat == EntityCategory.Monster)` — player HP for the HUD comes from the render-thread `PlayerVitals`, not the entity walk; player `EntityDot.HpCur/HpMax` are unused. (Grep `EntityDot` + `Player` + `Hp` to confirm no consumer; note in report.)

- [ ] **Step 5: Build + tests** (0 errors, 268 green).

- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "perf(stealth): render-thread redundant-read dedupes + CameraMatrix gate (SR-5)"
```
**Live scenario:** focused baseline rpmPerSec drops ~200–300/sec; HP bars / nameplates / player blip unchanged.

---

### Task SR-6: Slow-refresh player scalars (batch)

**Files:** Modify `Poe2Live.cs` (`PlayerName` cache). Modify `RadarApp.cs` (`PlayerLevel` + `PlayerVitals` refresh counters).

**Rationale:** Player name never changes (cache permanently); level changes ~once/min (5 s refresh); vitals feed only HUD %-bars (10 Hz is imperceptible, and there is no auto-flask needing full rate).

- [ ] **Step 1: (a) Cache PlayerName.** In `Poe2Live.PlayerName` (285–288):
```csharp
    private string? _cachedPlayerName;
    public string PlayerName(nint localPlayer)
    {
        if (_cachedPlayerName != null) return _cachedPlayerName;
        var c = PlayerComp(localPlayer);
        var name = c == 0 ? "" : ReadStdWString(c + Poe2.PlayerComponent.Name);
        if (!string.IsNullOrEmpty(name)) _cachedPlayerName = name;
        return name;
    }
```

- [ ] **Step 2: (b) Slow-refresh PlayerLevel.** In `RadarApp.WorldTick()` where `_charLevel = _live.PlayerLevel(localPlayer)` (grounding ~1560), add a counter field `private int _levelRefreshTick;` and gate:
```csharp
        if (_levelRefreshTick++ % 150 == 0) _charLevel = _live.PlayerLevel(localPlayer);
```
(150 world ticks ≈ 5 s; `_charLevel` keeps its last value between refreshes.)

- [ ] **Step 3: (c) Slow-refresh PlayerVitals.** At `RadarApp.cs:1273`, the render-thread vitals read. Add `private int _vitalsRefreshFrame;` and gate to ~every 5th render frame (~12 Hz at 60 fps):
```csharp
            if (_vitalsRefreshFrame++ % 5 == 0 && _liveRender.PlayerVitals(localPlayer) is { } v)
            { _hpPct = v.HpPct; _manaPct = v.ManaPct; _esPct = v.EsPct; }
```
(No auto-flask consumer; %-bar refresh at 12 Hz is imperceptible. `_hpPct/_manaPct/_esPct` keep last values between reads.)

- [ ] **Step 4: Build + tests** (0 errors, 268 green).

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): cache PlayerName, slow-refresh PlayerLevel + PlayerVitals (SR-6)"
```
**Live scenario:** HUD level/vitals visually identical; small steady rpmPerSec drop.

---

### Task SR-7: Atlas per-node feature gates (iconType + status reads)

**Files:** Modify `src/POE2Radar.Core/Game/Poe2Atlas.cs` (`ReadCanvasNodes` 412–428; add gate properties). Modify `RadarApp.cs` (`UpdateAtlas`, before `_atlas.ReadNodes`).

**Rationale:** When atlas content-icons are off, the `iconType` 5-level child-walk (~5 reads/node × ~1100) is pure waste (~330k reads/sec while atlas open); when routing + hide-filters are off, the DataStorage/status reads (~3/node) are waste (~99k reads/sec). Both feed the freeze-sig already, so toggling rebuilds correctly.

**Interfaces:** Produces `Poe2Atlas.ShowContentIcons` (bool) and `Poe2Atlas.NeedNodeStatus` (bool), set by RadarApp before each `ReadNodes`. (Poe2Atlas is in Core and cannot reference `RadarSettings` in Overlay — use plain bool properties.)

- [ ] **Step 1: Add gate properties** to `Poe2Atlas`:
```csharp
    /// <summary>Set by RadarApp before ReadNodes: skip the per-node content-icon child-walk when the
    /// content-icon overlay is off, and skip the accessible/completed status reads when no route/hide
    /// filter needs them. Both feed the freeze-signature, so toggling them rebuilds correctly.</summary>
    public bool ShowContentIcons { get; set; } = true;
    public bool NeedNodeStatus { get; set; } = true;
```

- [ ] **Step 2: Gate iconType.** In `ReadCanvasNodes` at lines 412–417, wrap the iconType child-walk:
```csharp
                var iconType = 0;
                if (ShowContentIcons)
                {
                    var d = el;
                    for (var lvl = 0; lvl < 5 && d != 0; lvl++)
                    {
                        if (_reader.TryReadStruct<uint>(d + Poe2.AtlasNode.Content, out var c) && c is > 0 and < 256) { iconType = (int)c; break; }
                        d = Ptr(Ptr(d + Poe2.UiElement.Children));
                    }
                }
```

- [ ] **Step 3: Gate status reads.** At lines 421–428, wrap:
```csharp
                bool accessible = false, completed = false;
                if (NeedNodeStatus)
                {
                    var storage = Ptr(el + Poe2.AtlasNode.DataStorage);
                    if (storage != 0)
                    {
                        var model = Ptr(storage + Poe2.AtlasNode.DataModel);
                        if (model != 0 && _reader.TryReadStruct<byte>(model + Poe2.AtlasNode.DataStatus, out var stb))
                        { accessible = (stb & 1) != 0; completed = (stb & 2) != 0; }
                    }
                }
```

- [ ] **Step 4: Drive from RadarApp.** In `UpdateAtlas`, before `_atlas.ReadNodes(inGameState)` (line 2945):
```csharp
        _atlas.ShowContentIcons = _settings.AtlasShowContentIcons;
        _atlas.NeedNodeStatus = _settings.AtlasAutoRoute || _settings.AtlasHideCompleted || _settings.AtlasHideAccessible;
```

- [ ] **Step 5: Build + tests** (0 errors, 268 green).

- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Atlas.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): gate atlas per-node icon + status reads on their features (SR-7)"
```
**Live scenario:** atlas open with AtlasShowContentIcons OFF + AtlasAutoRoute OFF → large rpmPerSec drop; marks/routes unchanged when those features are on.

---

### Task SR-1: Cache the static per-node atlas iconType (safe atlas read-cut)

**Files:** Modify `src/POE2Radar.Core/Game/Poe2Atlas.cs` (`ReadCanvasNodes` iconType block; `Invalidate`; add cache field).

**Rationale (safe form):** The audit's headline was the ~20k-read atlas re-walk every tick. Skipping the whole walk on unchanged inputs is UNSAFE (the freeze-sig needs live node positions to detect a pan; skipping would freeze off-screen arrows mid-pan — a visible regression). The safe, invisible capture: the expensive per-node cost is the `iconType` 5-level child-walk, and `iconType` (the content sigil) is **static per node** — it never changes while the panel exists. Cache it by element address (mirrors the entity component cache), so after the first pass the walk is skipped. Positions/status stay live (pan-following intact).

**Interfaces:** Internal cache only; no API change. Composes with SR-7 (only caches when `ShowContentIcons` is on).

- [ ] **Step 1: Add the cache field** near the other atlas caches (e.g. beside `_tagCache`):
```csharp
    private readonly Dictionary<nint, int> _iconTypeCache = new();  // element → content-icon type (static per node)
```

- [ ] **Step 2: Use the cache in the iconType walk.** Replace the SR-7 iconType block body so it checks the cache first:
```csharp
                var iconType = 0;
                if (ShowContentIcons)
                {
                    if (!_iconTypeCache.TryGetValue(el, out iconType))
                    {
                        var d = el;
                        for (var lvl = 0; lvl < 5 && d != 0; lvl++)
                        {
                            if (_reader.TryReadStruct<uint>(d + Poe2.AtlasNode.Content, out var c) && c is > 0 and < 256) { iconType = (int)c; break; }
                            d = Ptr(Ptr(d + Poe2.UiElement.Children));
                        }
                        _iconTypeCache[el] = iconType;
                    }
                }
```

- [ ] **Step 3: Clear the cache on canvas invalidation.** In `Poe2Atlas.Invalidate()` (grep for it — it's called when the cached canvas goes stale on close/reopen), add:
```csharp
        _iconTypeCache.Clear();
```
Also clear it anywhere `_tagCache` is cleared (they share the same element-address lifetime). Grep `_tagCache.Clear` and mirror.

- [ ] **Step 4: Build + tests** (0 errors, 268 green).

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Atlas.cs
git commit -m "perf(stealth): cache static per-node atlas iconType (skip the 5-level walk after first pass) (SR-1 safe)"
```
**Live scenario:** atlas open with content-icons ON, static view → rpmPerSec settles lower after the first second (walk cached); content icons render identically.

---

### Task SR-8: POI CompletedState one-way cache + slow-refresh in ReadIcon

**Files:** Modify `Poe2Live.cs` (`ReadIcon` 560–570; area-clear 452–457; `EvictEntity` 544–549; add fields).

**Rationale:** POI completion is a one-way transition (never reverts in a zone) — mirror the proven `_openedChests`/`ReadChestOpened` pattern. Once complete, never re-read; while incomplete, poll every ~10 ticks instead of every tick.

- [ ] **Step 1: Add fields** (near `_openedChests`, line 27):
```csharp
    private readonly HashSet<nint> _completedPois = new();       // POI confirmed complete (one-way; cleared per zone)
    private readonly Dictionary<nint, (int tick, bool poi, bool complete)> _iconState = new();  // last ReadIcon result + tick
    private int _iconTick;                                        // incremented once per Entities() pass
```

- [ ] **Step 2: Rewrite ReadIcon** (560–570) with the cache:
```csharp
    private (bool poi, bool complete) ReadIcon(nint entity)
    {
        if (_completedPois.Contains(entity)) return (true, true);          // one-way: never re-read a completed POI
        if (_iconState.TryGetValue(entity, out var last) && (_iconTick - last.tick) < 10)
            return (last.poi, last.complete);                             // slow-refresh: reuse recent result
        if (!_iconAddr.TryGetValue(entity, out var icon))
        {
            icon = ResolveComponent(entity, "MinimapIcon");
            _iconAddr[entity] = icon;
        }
        if (icon == 0) { _iconState[entity] = (_iconTick, false, false); return (false, false); }
        var complete = _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var s) && s != 0;
        if (complete) _completedPois.Add(entity);
        _iconState[entity] = (_iconTick, true, complete);
        return (true, complete);
    }
```

- [ ] **Step 3: Advance the icon tick once per pass.** In `Entities()` where the per-pass budgets reset (lines 472–473), add:
```csharp
        _iconTick++;
```

- [ ] **Step 4: Clear on zone change + eviction.** In the area-clear block (454–455) add `_completedPois.Clear(); _iconState.Clear();`. In `EvictEntity` (546–548) add `_completedPois.Remove(entity); _iconState.Remove(entity);`. Also add `_completedPois.Clear(); _iconState.Clear();` at the rebind clear (line 95).

- [ ] **Step 5: Build + tests** (0 errors, 268 green).

- [ ] **Step 6: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "perf(stealth): one-way cache + 10-tick slow-refresh for POI CompletedState (SR-8)"
```
**Live scenario:** zone with expedition/shrine POIs → completing one stops its per-tick icon read; POI dots + completion visuals unchanged.

---

### Task SR-9: ReadMonolith static-data cache (live-read only Collected)

**Files:** Modify `Poe2Live.cs` (`ReadMonolith` 583–624; area-clear; `EvictEntity`; add cache). Depends on SR-8 (reuses `ReadIcon`'s cache for `Collected`).

**Rationale:** A monolith's HoleCount/AnchorIdx/AnchorPos/IsUnique are fixed at placement; only `Collected` (via `ReadIcon`) changes. Cache the static chain-walk result per device; re-read only `Collected`.

- [ ] **Step 1: Add cache field** (near other per-entity caches):
```csharp
    private readonly Dictionary<nint, MonolithState> _monolithCache = new();  // device → static station data (Collected re-read live)
```

- [ ] **Step 2: Short-circuit ReadMonolith on cache hit.** At the top of `ReadMonolith(nint device)` (after computing `collected` via `ReadIcon`), add:
```csharp
        var (_, collected) = ReadIcon(device);
        if (_monolithCache.TryGetValue(device, out var cachedMono))
            return cachedMono with { Collected = collected };
```
Then at each successful `return new MonolithState(...)` that represents a resolved station (the `Resolved: true` returns at lines 612, 618, 623), store it before returning, e.g.:
```csharp
        var result = new MonolithState(true, holes, idx, pos, false, collected);
        _monolithCache[device] = result;
        return result;
```
Only cache `Resolved == true` results (never cache the `fail` early-returns, so a transient miss retries). Store `with { Collected = collected }` semantics: cache carries the static fields; the returned/refreshed `Collected` always comes from the live `ReadIcon`.

- [ ] **Step 3: Clear on zone change + eviction.** Add `_monolithCache.Clear();` to the area-clear block + rebind; `_monolithCache.Remove(entity);` to `EvictEntity`.

- [ ] **Step 4: Build + tests** (0 errors, 268 green).

- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Core/Game/Poe2Live.cs
git commit -m "perf(stealth): cache monolith static station data, live-read only Collected (SR-9)"
```
**Live scenario:** Monoliths ON in a zone with runeforge monoliths → per-tick monolith read cost drops after first resolve; reward panel unchanged.

---

### Task SR-10: Cull off-screen atlas marks before the render TryRelPos loop

**Files:** Modify `RadarApp.cs` (`Tick()` atlas-mark loop, lines 1320–1324).

**Rationale:** Off-screen marks are never drawn, but the render thread still reads their live `relPos` (~2 reads × ~50 off-screen marks × 60 Hz ≈ 6,000 reads/sec). Skip `TryRelPos` for marks whose baked position projects off-screen; use the baked position (they aren't drawn anyway).

- [ ] **Step 1: Cull in the mark loop.** At `RadarApp.cs:1320–1324`, replace the `foreach (var m in ar.Marks)` body with a screen-bounds check using the baked position (`m.X`/`m.Y` are baked centre coords) and the live atlas projection scale:
```csharp
                float pscale = (_window.Height > 0 ? _window.Height / 1600f : 0.675f) * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
                foreach (var m in ar.Marks)
                {
                    float bsx = m.X * pscale, bsy = m.Y * pscale;
                    bool onScreen = bsx > -200 && bsx < _window.Width + 200 && bsy > -200 && bsy < _window.Height + 200;
                    _atlasMarkFrame.Add(
                        onScreen && m.Element != 0 && _liveRender.TryRelPos(m.Element, out var mx, out var my)
                            ? m with { X = AtlasGeometry.AtlasCentre(mx, m.W), Y = AtlasGeometry.AtlasCentre(my, m.H) }
                            : m);
                }
```
(Confirm `m.X`/`m.Y` are the baked centre in the same coordinate space `pscale` expects — grounding shows marks are built with `AtlasGeometry.AtlasCentre(n.X, n.W)` and the render projects `mark * pscale`; match the renderer's exact projection. If the mark stores pre-centre `n.X`, adjust. Verify against `OverlayRenderer`'s atlas-mark projection and note in the report.)

- [ ] **Step 2: Build + tests** (0 errors, 268 green).

- [ ] **Step 3: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): cull off-screen atlas marks before live relPos read (SR-10)"
```
**Live scenario:** atlas open, zoomed so many marks are off-screen → rpmPerSec lower; on-screen rings/arrows track the pan identically.

---

### Task SR-11: Slow-refresh CurrentNodeGrid to ~1 Hz

**Files:** Modify `RadarApp.cs` (`UpdateAtlas` line 2972; add counter + cache fields).

**Rationale:** `CurrentNodeGrid()` (~4 reads) changes only when the player enters a new map (minutes apart). Refresh every ~30 world ticks (~1 s); the auto-route re-solves within a tick of the refresh.

- [ ] **Step 1: Add fields** near the atlas state fields:
```csharp
    private int _curGridRefreshTick;
    private (int X, int Y)? _cachedCurGrid;
```

- [ ] **Step 2: Slow-refresh in UpdateAtlas.** At line 2972, replace `var curGrid = _atlas.CurrentNodeGrid();` with:
```csharp
        if (_curGridRefreshTick++ % 30 == 0) _cachedCurGrid = _atlas.CurrentNodeGrid();
        var curGrid = _cachedCurGrid;
```
(`curGrid` feeds the freeze-sig at line 3002 and `currentPt` at 3136 — a ≤1 s stale current-node is imperceptible since map transitions take seconds. The sig stays stable across the 29 skipped ticks, which is correct.)

- [ ] **Step 3: Build + tests** (0 errors, 268 green).

- [ ] **Step 4: Commit.**
```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "perf(stealth): slow-refresh atlas CurrentNodeGrid to ~1 Hz (SR-11)"
```
**Live scenario:** atlas auto-route source still updates within ~1 s of entering a new map; small rpmPerSec drop while atlas open.

---

### Task SR-12: Version bump + integration + compliance

**Files:** Modify `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (`<Version>` → `0.18.0`).

- [ ] **Step 1: Bump version** `0.17.0` → `0.18.0`.
- [ ] **Step 2: Full build** `dotnet build -c Debug` → 0 CS errors across all projects.
- [ ] **Step 3: Full tests** `dotnet test tests/POE2Radar.Tests` → 268 green.
- [ ] **Step 4: Compliance** `pwsh scripts/compliance-gate.ps1` (or `powershell -File`) → PASS; `scripts/scrub-strings.ps1 -SelfTest` → PASS.
- [ ] **Step 5: Commit.**
```bash
git add src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
git commit -m "chore(release): bump version to 0.18.0 (Performance v3 — Stealth Reads)"
```

---

## Self-Review

**Spec coverage:** SR-1 (safe iconType cache) ✓, SR-2 idle-slowdown ✓, SR-3 mod-gate ✓, SR-4 item-gate ✓, SR-5 render dedupes (AreaHash/player/camera/playerHP) ✓, SR-6 player-scalar slow-refresh ✓, SR-7 atlas feature-gates ✓, SR-8 POI cache ✓, SR-9 monolith cache ✓, SR-10 mark cull ✓, SR-11 curGrid slow-refresh ✓, version ✓. The audit's "auto-flask stays full-rate" is moot (no auto-flask in this fork) — SR-6 slow-refreshes vitals freely; noted in Global Constraints.

**Guardrail:** every task is invisible or imperceptible; SR-1 deliberately uses the safe cache form (not the pan-unsafe whole-walk skip). Auto-flask N/A. All published shapes unchanged.

**Type consistency:** `EnableModReads`/`EnableItemIdentityReads` (Poe2Live bools), `ShowContentIcons`/`NeedNodeStatus` (Poe2Atlas bools), `_iconTypeCache`/`_completedPois`/`_iconState`/`_monolithCache` (Poe2Live/Poe2Atlas caches, cleared in area-clear + EvictEntity + Invalidate), refresh counters (`_levelRefreshTick`/`_vitalsRefreshFrame`/`_curGridRefreshTick`/`_iconTick`) — consistent across tasks.
