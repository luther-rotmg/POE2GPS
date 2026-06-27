# Performance & Footprint Audit — 2026-06-27

Head-to-toe audit run after v0.7.1, six parallel auditors over: world hot path, render hot path,
allocations, threading/cadence, memory residency, and a re-validation of the prior
`docs/audit-2026-06-22.md` deferrals. Priority (user, verbatim): *"leave the lightest resource
footprint possible."* For this project **footprint** = (a) ReadProcessMemory syscalls/sec, (b)
per-tick managed allocations / GC pressure, (c) CPU on the render + world threads, (d) idle RSS.

Findings are split into two buckets by **gating**:

- **SOLO** — behavior-preserving refactor, buildable + testable offline, no change to *what* memory
  is read. Shipped in **v0.8.0**.
- **IN-GAME** — changes the memory *read pattern* (cadence or layout). Biggest raw-syscall wins, but
  each needs a live entity/map view to confirm every value still resolves. **Deferred** to the
  in-game validation session (`docs/superpowers/2026-06-26-ingame-validation-session.md`).

All anchors below were re-verified against live code on 2026-06-27.

---

## Bucket 1 — SOLO (ship in v0.8.0)

### Idle memory (biggest footprint win)

1. **Lazy mod-translation tables** — `ItemModTranslator.Shared` (`ItemModTranslator.cs:36`) and
   `ModRanges.Shared` eager-load three embedded JSON tables (`poe2_mod_stats.json` 1.5 MB,
   `poe2_stat_descriptions.json` 1.1 MB, `poe2_mod_ranges.json` 2.8 MB) at first class use →
   ~15–20 MB resident managed heap **even when the gear scorer is OFF (default)**. Gate behind
   `Lazy<T>` initialised only on first gear-scan use. `_renderedIndex` (`:45`) is already lazy +
   Research-only; confirm no live path calls `StatIdsForRenderedLine`.

2. **Unbounded session accumulators** — `SeenPoiLog` (`Web/SeenPoiLog.cs:17`), `EntityAtlasLog`
   (`Web/EntityAtlasLog.cs:18`), `ModCatalog` (`Web/ModCatalog.cs:22`) grow monotonically with no
   cap. Add a max-entry cap (e.g. 5 000 / 10 000 / 2 000); switch `ModCatalog` `SortedSet→HashSet`
   (sort only at Save).

### Render-frame allocations (fire at FpsCap, up to 144+ Hz)

3. **`AtlasProjection()` `double[8]`/frame** (`RadarApp.cs:1595-1600`) — reuse a field/struct; only
   two scalars are ever non-zero. *Flagged in the prior audit, still open. Highest-frequency alloc.*
4. **`DrawSessionHud` per-frame `List` + interpolated strings** (`OverlayRenderer.cs:591`) — cache
   the formatted lines; rebuild only when the underlying seconds-resolution values change.
5. **`ParseColor` per entity + per landmark per frame** (`OverlayRenderer.cs:826,847`) — cache the
   parsed `Color4` on `DisplayRule` (same pattern HP bars already use via `PackColor`).
6. **Atlas-route point lists per frame** (`RadarApp.cs:983-996`) — reuse render-thread scratch lists
   (same idiom as the existing `_atlasMarkFrame` / `_hpFrame`).
7. **`Probe()` `new List<nint>(13)`** (`Poe2Live.cs:156`) — hit at render rate via `TryResolve`;
   make it a reusable per-instance field cleared on entry.
8. **`DrawMonolithPanel` `OrderByDescending+Take+ToList`/frame** (`OverlayRenderer.cs:552`) — sort +
   cap once at publish time (world thread), store the capped list in `MonolithRender`.

### Threading / CPU

9. **Render-thread data race (correctness):** `Tick()` calls `_live.PlayerVitals` — the **world**
   reader stack — at `RadarApp.cs:937`, concurrent with `WorldTick`'s use of the same non-thread-safe
   instance. Switch to `_liveRender`. Identical observable behavior; removes the race.
10. **Redundant `_campaign.Rank()` 2–3×/tick** (`RadarApp.cs:1271-1301`) — `BuildRankedTargets`,
    `DirectorReconcile`, and `CampaignReconcile` each recompute it. Compute once per tick, pass in.
11. **`BuildNavTargets` LINQ + `OrderBy` + double Resolve** (`RadarApp.cs:1635-1647`) — single-pass
    `foreach`; memoize `_displayRules.Resolve(e)` per tick (shared with `BuildHpSpecs`); drop the
    `OrderBy` (the F6 nearest-scan re-sorts anyway).
12. **AreaHash / AreaLevel re-read per tick** (`RadarApp.cs:1147-1148`) — cache per `areaInstance`
    (zone-stable); re-read only when the instance address changes.
13. **Spin-wait pacer** (`RadarApp.cs:681`) — use `Stopwatch.GetTimestamp()` vs a precomputed target
    tick; trim the busy-spin window. Reclaims ~1 ms/frame of a core at 144 Hz.
14. **`ScanLootLabels` BFS allocates `Queue`+`HashSet`+`List`/call** (`Poe2Live.cs:1282-1312`) —
    hoist the `Queue`/`HashSet` to reusable fields (same pattern as `_entQueue`/`_entVisited`).
15. **`DirectorReconcile` no throttle** (`RadarApp.cs:1292`) — gate to ~4 Hz (decision changes rarely;
    forced on zone change).
16. **`UpdateAtlas` median-sort LINQ/tick while atlas open** (`RadarApp.cs:2263-2264`) — reuse a
    `List<float>` buffer + in-place `Sort`.
17. **`ActiveCycleList` allocation per cycle event** (`RadarApp.cs:1655-1662`) — publish a
    `_defaultCycleTargets` list from the world thread; return it directly.
18. **`std::map` walk cap flat 200 000** (`Poe2Live.cs:425`) — bound to `size*4 + 1024` so a corrupt
    tree size can't stall the world thread for hundreds of ms.

### Low / hygiene (fold in where the same file is already open)

19. `ReconcileTrackers` LINQ `.Where().ToList()` (`RadarApp.cs:1971`); `ToggleSelectionCore`
    `string.Join` inside `_navLock` (`RadarApp.cs:1945`); `Landmark.Key` interpolation per access
    (`Poe2Live.cs:138`) → cache on the record; `SnapshotSelection` double-lock per tick
    (`RadarApp.cs:1301,2030`) → return the snapshot from `MaintainRoutes`; `DiscoverMapElements` BFS
    per-area alloc (`Poe2Live.cs:1028-1055`); atlas chunk 1 MB buffers (`Poe2Atlas.cs:133,200`) →
    fields; vital-offset re-latch guard (`Poe2Live.cs:336`, correctness — clear
    `_vitalOffsetsResolved` when a latched read returns `HpMax==0 || HpCur>HpMax`).

---

## Bucket 2 — IN-GAME (deferred to validation session)

These are the **largest raw-syscall reductions** but they rewrite how memory is read; each needs a
live view to confirm every component/field still resolves before shipping.

- **`ResolveComponent` bulk-read** (`Poe2Live.cs:1442-1468`) — bulk-read the contiguous
  ComponentLookUp bucket once, match all needed names in-process. Collapses ~9 bucket walks → 1 RPM
  per new entity (the highest per-pack win; ~50–140 string allocs/entity eliminated). *Flagged in the
  prior audit as the #1 item, still open.*
- **`ReadReaction` value cache** (`Poe2Live.cs:728-737`) — Reaction is static per spawn; add a
  `_reaction` dict like `_rarity`. ~12 k RPM/s today on a 400-entity map. Needs a slow-cadence refresh
  for the rare conversion case → in-game.
- **`TryReadMapElement` single bulk read** (`Poe2Live.cs:1057-1065`) — one `TryReadBytes` over
  `[Flags…Zoom]` instead of 4 RPM/element/frame (~1.2 k syscalls/s at 144 Hz).
- **`ReadChestOpened` one-way cache** (`Poe2Live.cs:1375-1380`) — opened flag never reverts; cache it.
- **`PlayerVitals` world-thread split** (`RadarApp.cs:1157`) — world thread only needs the
  offset-heal side-effect; expose `EnsureVitalOffsets` so it stops reading+discarding 3 vitals/tick.
- **`ReadMods` bulk-read** (`Poe2Live.cs:614-628`) — bulk-read the mod array; `HashSet` dedup.
- **`Entities()` list reuse** (`Poe2Live.cs:415`) — fill a caller-owned reused list instead of
  `new List<EntityDot>(256)`/tick (publishing-lifetime concern → validate live). The simpler
  capacity-hint variant (`Math.Max(64, prev)`) is solo and folded into Bucket 1 task scope.
- **Terrain raw-buffer `ArrayPool`** (`Poe2Live.cs:958`) — pool the intermediate packed grid (one LOH
  alloc/zone-change).

---

## Already closed (verified by prior-batch auditor)

- WorldLoop `PublishEmptyWorld` on thrown tick + rate-limited stderr — shipped in v0.7.1
  (`RadarApp.cs:708-717`).
- `_lastAtlasSig` gate on route-list rebuild — already present (`RadarApp.cs:2303-2305`).
