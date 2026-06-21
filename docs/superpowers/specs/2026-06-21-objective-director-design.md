# Objective Director ("Campaign Director") — Design Spec

- **Date:** 2026-06-21
- **Status:** Draft for review
- **Topic:** A read-only, per-zone objective director for POE2GPS that auto-selects + routes to the highest-priority in-zone objective, reusing the existing navigation pipeline.

---

## 1. Summary

When enabled, the **Objective Director** watches the zone you're in, picks the **highest-priority objective present** (from a community-editable catalog), and routes you to it — advancing to the next as you complete them. It **selects targets and draws routes only; it never sends input.** You walk it.

It is built as an **additive module** that generalizes a mechanism Sikaka already has (`OnAreaChanged` auto-selects navigable targets each zone) and reuses his id-based selection → A* routing → renderer pipeline end to end. This keeps POE2GPS sync-compatible with upstream.

## 2. Goal & non-negotiable invariants

- **I1 — Read-only.** The director only mutates the nav *selection* (a list of string ids) and reads game state. No `SendInput`, no `WriteProcessMemory`. The compliance gate (`scripts/compliance-gate.ps1`) must still pass — this feature adds no forbidden symbols.
- **I2 — Reuse, never rebuild routing.** The director selects by id and lets the existing `MaintainRoutes`/`BackgroundReplanner`/`PathPlanner`/`RouteTracker` compute and draw the route. It must NOT enqueue its own replans or build paths.
- **I3 — Sync-safe footprint.** New files + one data file; the only edits to Sikaka's hot files are a single `WorldTick` hook, one `RadarSettings` field, the `ReadSettings`/`ApplySettings` toggle lines, and one `RadarState` field + `/state` projection. All documented in `docs/upstream-merge.md`.
- **I4 — No new identifying data.** The dashboard payload must not reintroduce the character name (it was deliberately removed for stealth — `RadarState.CharName` is published as `""`).

## 3. Verified integration facts (the grounding)

Confirmed by reading `main`:

- **Nav selection API (the reuse surface).** `RadarApp.cs:1719-1739`: `GetNavSelection() → IReadOnlyList<(string Id,int Slot)>`, `ToggleNavTarget(string id)`, `ClearNavSelection()` — all `_navLock`-guarded. Core mutator `ToggleSelectionCore(string id)` (`RadarApp.cs:1479`) edits `_selectedIds` only (cap `MaxSelectedTargets=8`). **There is no grid/address setter — you select by stable string id.**
- **Id format.** `e:<entityId(uint)>` or `t:<landmarkKey>` (`RadarApp.cs:1278-1280`). `TryResolveTargetGrid(id)` (`:1558-1580`) resolves an id to a live grid each tick by scanning `_entities` (by `Id`) or `_landmarks` (by `Key`). **Any entity in `_entities` is selectable by `e:<id>`, not just POIs.**
- **Routing/drawing is automatic once an id is selected.** `MaintainRoutes(player)` (`WorldTick:1024`, body `:1527-1648`) creates a `RouteTracker` per id, enqueues the off-thread A* replan, and `RebuildSelectedPaths` publishes the drawn route. The director must not touch any of this.
- **The existing analog (what the director generalizes).** `OnAreaChanged` (`RadarApp.cs:1331-1364`): on zone change, clears `_selectedIds` then auto-adds every `NavTarget` with `AutoPath==true` (i.e. `DisplayRule.Navigable`), capped at 8. `PruneCompletedTargets()` (`:1383-1397`, called `WorldTick:1019`) drops selected `e:` ids whose entity is present **and** `IconComplete` — **live completion already exists.**
- **Hook point.** `WorldTick(...)` (`RadarApp.cs:921-1034`, ~30 Hz, world thread). Between `BuildNavTargets` (`:1005`) and `MaintainRoutes` (`:1024`), in scope: `_entities` (`List<Poe2Live.EntityDot>`), `_landmarks` (`IReadOnlyList<Poe2Live.Landmark>`), `_navTargets` (`List<NavTarget>`), `player`, `areaCode/areaHash/areaLevel`. This is the director's insertion point.
- **Match surface.** `Poe2Live.EntityDot` (`Poe2Live.cs:61-85`): `Id, Grid, World, Category, Metadata, HpCur/Max, Poi, Reaction, Rarity, Opened, IconComplete, Mods, ItemArt/Name`. `Poe2Live.Landmark` (`:92-99`): `Name, Path, Center, TileCount, CuratedName, Key`. The canonical allocation-free matcher over `EntityDot` is `DisplayRules.Compiled.Matches(in EntityDot)` (`DisplayRules.cs:319-337`) — the idiom to mirror.
- **Data-file pattern** (`WatchedEntities.cs:27-173`): embedded default seed → written to `ConfigDir` on first run → `_gate` lock + volatile immutable snapshot for lock-free reads → `Save()` camelCase JSON. Constructed in the `RadarApp` ctor (`~:255`). Embedded resource declared in the Overlay csproj (`:28`).
- **Settings + dashboard patterns** (verified, recipe in §6/§8): `RadarSettings` bool prop; `ApiServer.ReadSettings`/`ApplySettings` lines; `DashboardHtml` `data-set` checkbox; per-tick panel via a `RadarState` field projected into `/state` and rendered like the `monoCard`.

## 4. Architecture & data flow

```
WorldTick (world thread, ~30Hz)
  ... BuildNavTargets (1005) ... PruneCompletedTargets (1019) ...
  ─▶ if settings.EnableDirector:  ObjectiveDirector.Reconcile(entities, landmarks, player, areaCode)
        │     match live entities/landmarks against ObjectiveCatalog (compiled, allocation-free)
        │     rank present matches: priority desc, then distance asc
        │     active = top match;  build its id (e:<Id> / t:<Key>)
        │     set selection to {active} via the existing _selectedIds layer (under _navLock)
        │     publish ranked queue (active + remaining) for the dashboard
  ─▶ MaintainRoutes (1024)  →  A* + draw the route to the selected id  (unchanged, reused)
```
Live, event-driven: re-selection fires on **zone change** and when the **active objective completes/disappears** (reusing `PruneCompletedTargets` + live id-resolution). Between triggers the selection is left alone, so a **manual pick persists** (override respected).

## 5. Components / file structure

**New (isolated):**
- `Core/Campaign/ObjectiveCatalog.cs` — the catalog model + a compiled, allocation-free matcher over `EntityDot`/`Landmark` (mirrors `DisplayRules.Compiled`). **Pure logic → unit-tested.**
- `Overlay/Web/CampaignObjectives.cs` — the user store (embedded seed → `ConfigDir/campaign_objectives.json`, `_gate` lock + volatile snapshot, `Add/Update/Remove/Import` → `Rebuild()+Save()`), modeled on `WatchedEntities.cs`.
- `Overlay/Web/default_campaign_objectives.json` — embedded default seed (registered in the Overlay csproj).
- `Overlay/Navigation/ObjectiveDirector.cs` — the per-tick reconciler: rank present matches, set the active selection, expose the queue. Holds no routing logic.

**Edits to shared files (the entire blast radius — all documented for upstream merges):**
- `RadarApp.cs` — construct the store in the ctor (`~:255`); one `Reconcile(...)` call in `WorldTick` between `:1019` and `:1024`; add a `Director` field to the published `RadarState` (`~:820`).
- `Config/RadarSettings.cs` — `public bool EnableDirector { get; set; } = false;`
- `Web/ApiServer.cs` — `enableDirector` in `ReadSettings`; a case in `ApplySettings`; the `director` projection in the `/state` object.
- `Web/DashboardHtml.cs` — one settings checkbox; one left-rail card + a `renderState()` block.

## 6. The catalog (data — the upkeep surface)

A priority-ranked list, matched against the live entity/landmark fields the radar already reads. Matcher fields mirror `DisplayRule` (ANY-of semantics, `?`=any), compiled once for allocation-free per-entity matching:

```json
[
  { "id": "runes-of-aldur", "label": "Runes of Aldur event", "category": "League",
    "priority": 100, "enabled": true,
    "match": { "metadata": ["RunesOfAldur"], "poi": "Yes" } },

  { "id": "passive-boss", "label": "+2 Passive boss", "category": "PassivePoints",
    "priority": 90, "enabled": true,
    "match": { "metadata": ["*Ascendancy*", "*Trial*"] } },

  { "id": "tile:arena", "label": "Boss arena (tile)", "category": "Bosses",
    "priority": 80, "enabled": true,
    "match": { "landmarkPath": ["*Arena*"] } }
]
```
- **Match surface:** entity objectives match `EntityDot.Metadata` (substring/glob, ANY-of) + optional `category`/`poi`/`rarity`; tile objectives match `Landmark.Path`/`CuratedName`. Position for distance ranking comes from `EntityDot.Grid` / `Landmark.Center`.
- Community-editable, import/export — same affordances as watchlists/landmarks.
- **Honest caveat (the real constraint):** an objective is only targetable if its entity/landmark is in the live `_entities`/`_landmarks` set. v1's seed catalog is bounded by **what your radar actually surfaces** — we'll confirm your priority content (seasonal event, passive bosses) qualifies during planning. Content the radar doesn't yet detect is out of v1 (and is really an upstream-detection contribution to Sikaka).

## 7. Director logic

- **Rank:** among enabled catalog entries with a present match in the zone, sort by `priority` desc, then nearest (`distance(player, grid)`) asc. The top match is the **active** objective; the rest are the **queue**.
- **Select:** set the nav selection to the single active objective's id (`e:`/`t:`), via the existing `_selectedIds` layer under `_navLock` (the same way `OnAreaChanged` does). v1 routes **one objective at a time** (matches the "event → then bosses" mental model); multi-route (top-N) is a later config knob.
- **Advance / complete:** re-select on zone change and when the active objective is gone/`IconComplete` (reusing `PruneCompletedTargets` + live id-resolution). No persisted state.
- **Manual override:** between triggers the director leaves the selection alone, so a manual F-key/dashboard pick sticks until the next trigger or zone change.
- **Interaction with the existing AutoPath auto-add:** when the director is enabled it is the **auto-selection authority** — it supersedes `OnAreaChanged`'s flat "add all navigable" selection (plan decides whether to gate that branch on `!EnableDirector` or let the director reconcile over it; the spec mandates only that the two never fight). When disabled, behavior is exactly upstream's.

## 8. UX

- **Toggle:** an "Objective Director" checkbox in the dashboard Settings (`data-set="enableDirector"`), **off by default**. Optional hotkey later.
- **Route:** the existing smoothed A* line — no new rendering.
- **Panel:** a left-rail "Objective Director" card mirroring `monoCard` — shows the **active objective** + the **ranked queue** for the zone — fed by a `Director` field added to `RadarState` and projected into the 1 Hz `/state` poll. (No new endpoint needed; payload carries no identifying data — §I4.)

## 9. Compliance & sync-safety

- Read-only end to end; the gate passes (no input/write symbols). The director only edits a list of string ids and reads state.
- Footprint: 4 new files + 1 data file + the small shared-file hooks in §5. `docs/upstream-merge.md` gets a "Objective Director" section listing those hook sites so a future `git merge upstream/main` re-applies them deterministically.
- Could later be offered upstream to Sikaka as an opt-in (it's a clean generalization of his `AutoPath`) — but it ships first as a POE2GPS module.

## 10. Testing

- **`ObjectiveCatalog` matching** — real xUnit tests in the existing `tests/POE2Radar.Tests`: synthetic `EntityDot`/`Landmark` snapshots → expected ranked objective order (priority + distance tiebreak; enabled/disabled; glob/substring matching; no-match → empty).
- **Director selection** — table tests over fake objective sets: correct active pick, queue order, advance-on-completion, manual-override persistence.
- **Live behavior** — manual release-checklist item (needs a running PoE2): enter a zone with a known objective, confirm it auto-routes, complete it, confirm advance.

## 11. Risks / open

- **Detection coverage** (the real one): bounded by what the radar already surfaces; verified per priority-objective during planning.
- **Director ↔ AutoPath interaction:** the one shared-behavior change; must be implemented so the two never fight (§7), and documented for merges.
- **Seasonal churn:** league content changes each season → the catalog (data only) is the upkeep surface.
- **Allocation discipline:** matching runs per-entity-per-tick on the world thread — must be compiled/allocation-free like `DisplayRules.Compiled`, and thread-safe (lock + volatile snapshot), or it risks GC churn / torn reads.

## 12. Out of scope (v1)

- Cross-zone / campaign-act progression GPS (a separate, much larger world-graph project — possible v2 on top of this).
- Persistent per-character completion history (the off-by-default nicety; live detection makes it unnecessary for the core feature, and it reintroduces the rename fragility).
- Contributing the director upstream to Sikaka (possible later; ships as a POE2GPS module first).
