# Catalog Builder — Design Spec

- **Date:** 2026-06-21
- **Status:** Approved 2026-06-21
- **Topic:** The Objective Director's detection & cataloging system — a persistent "seen POI" log + a dashboard that surfaces uncatalogued content and turns it into Director objectives with one click.

---

## 1. Summary

The Objective Director (shipped dormant in v0.1.1) only routes content the radar already detects, and its seed catalog is thin. The **Catalog Builder** makes growing that coverage a repeatable in-session loop:

> play → the overlay **logs** every notable thing you encounter → the dashboard shows you what's **not yet catalogued** → one click turns it into a Director objective (you pick category + priority).

It cannot do the live signature research for you (that needs a running PoE2 client), but it makes "catalogue every relevant POI" a ~10-second-per-item workflow that stays current as league content shifts. It is **read-only** (reads entity metadata, writes local JSON — exactly like the existing `ModCatalog`) and additive/sync-safe.

## 2. Goal & non-negotiable invariants

- **I1 — Read-only.** Reads entity/landmark data; writes only local config JSON. No input-emission or process-write APIs. `scripts/compliance-gate.ps1` stays green.
- **I2 — Reuse existing machinery, no duplication.** The accumulator mirrors `ModCatalog` exactly; classification reuses the Director's `ObjectiveCatalog` compiled matcher; friendly names reuse `EntityNameResolver`/`ZoneGuide`; the catalog edits go through the existing `CampaignObjectives` store.
- **I3 — No identifying data.** The dashboard payloads carry no character name (consistent with the stealth posture).
- **I4 — Sync-safe footprint.** New isolated files + a tiny `ObjectiveCatalog` addition + small hooks (one `WorldTick` observe, one `Dispose` flush, ApiServer wiring, a dashboard tab), all documented in `docs/upstream-merge.md`.
- **I5 — The tool captures + surfaces; the human judges.** It never auto-decides relevance, category, or priority — you do.

## 3. Verified integration facts (grounding)

Confirmed by reading `main`:

- **Accumulator template — `ModCatalog`** (`Overlay/Web/ModCatalog.cs`): `_gate` lock + a collection + a `Stopwatch _sinceDirty` + `bool _dirty` + `const long FlushAfterMs = 4000`. `Observe(IEnumerable<Poe2Live.EntityDot>)` loops then `MaybeFlush()`; `MaybeFlush` saves only after the debounce window; `Flush()` drains on shutdown; `Load`/`Save` are JSON round-trips to a `ConfigDir` path. Constructed in the `RadarApp` ctor (`RadarApp.cs:488`), `Observe` called in `WorldTick` at `RadarApp.cs:957` (on the already hidden/self-filtered `_entities`), `Flush()` in `Dispose` (`RadarApp.cs:2149`). Read endpoint `/api/mods` (`ApiServer.cs:327-331`) returns `{ mods = _knownMods() }` via a ctor-injected `Func<IReadOnlyList<string>>` (`ApiServer.cs:54,78,98`; wired `() => _modCatalog.All` at `RadarApp.cs:490-492`).
- **Friendly names:** `EntityNameResolver.Shared.ResolveOrShorten(metadata)` (`Core/Game/EntityNameResolver.cs:84`) — never null, best-effort label from `entity_names.json`; used in `/entities` at `ApiServer.cs:213`. Zones: `ZoneGuide.Shared.FriendlyName(areaCode)` (`Core/Game/ZoneGuide.cs`), used in `/state`.
- **Match surface:** `Poe2Live.EntityDot` (`Id, Grid, Category, Metadata, Poi, Rarity, …`); `EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }` (7 members); `Poe2Live.Landmark { Name, Path, Center, CuratedName, Key }`.
- **`ObjectiveCatalog`** (`Core/Campaign/CampaignObjective.cs`): `_compiled` is enabled-only; the `Compiled` matcher exposes `MatchesEntity(in EntityDot)` / `MatchesLandmark(string)`. A public `Covers(in EntityDot)` / `Covers(string path)` is a 3-line reuse (`foreach (var c in _compiled) if (c.MatchesEntity(in e)) return true;`).
- **`CampaignObjectives` store** (`Overlay/Web/CampaignObjectives.cs`): `All`, `Add(CampaignObjective)` (upsert by Id), `Remove(string id)`, `Rank(...)`, under `_gate` + volatile `_snapshot`. Constructed in `RadarApp` for the director's `Rank`, **but not yet passed to `ApiServer`** — the Catalog Builder wires it in.
- **Dashboard tab pattern** (`DashboardHtml.cs:404-409,624-633`): a `<button class="tab" data-tab="X">` in `.tabs`, a `<section class="view" data-view="X" hidden>`, and a generic switch that toggles `.on`/`hidden` and calls `load<X>()`. CSS already handles show/hide.
- **Endpoint patterns** (`ApiServer.cs`): READ via injected `Func` (`/api/mods`); WRITE via GET+POST case that loopback-gates (`IsLoopbackHost`, `:832`), `ReadBody`, `JsonDocument.Parse`, then mutates the store + `SanitizeRule`-style validation (model: `/api/display-rules` `:422-440`, `/api/landmarks` `:395-420`).

## 4. Architecture & data flow

```
world tick ─▶ SeenPoiLog.Observe(_entities, _landmarks, areaCode)        (accumulator, mirrors ModCatalog)
                   │  PoiCandidate.IsCandidate filters to notable things; dedups by Signature;
                   │  records metadata|landmarkPath, category, poi, rarity, friendlyName, firstZone, count, lastSeen;
                   │  debounced flush → config/seen_pois.json
                   ▼
   dashboard GET /api/seen-pois ──▶ each entry tagged covered? via CampaignObjectives.Covers(...) (ObjectiveCatalog matcher)
                   ▼
   Director tab → "Needs cataloguing" (uncatalogued first, friendly name + metadata + category + zone + count)
                   │  (you pick category + priority; match term pre-filled from the entry)
                   ▼
   POST /api/objectives {add:{…}} ──▶ CampaignObjectives.Add(objective) ──▶ director uses it next tick (already wired)
   "Catalog" sub-view: GET list, POST {remove:{id}} / reorder / toggle.
```

## 5. Components / file structure

**New, isolated:**
- `Core/Campaign/PoiCandidate.cs` — **pure** logic (so it's unit-testable against `tests/POE2Radar.Tests`): `bool IsCandidate(in Poe2Live.EntityDot)` (the filter), `string Signature(in Poe2Live.EntityDot)` / `Signature(Poe2Live.Landmark)`, and the `SeenPoi` record `{ Signature, Metadata?, LandmarkPath?, Category, Poi, Rarity, FriendlyName, FirstZone, Count, LastSeenUtc }`.
- `Overlay/Web/SeenPoiLog.cs` — the accumulator (I/O), **mirroring `ModCatalog`**: `Observe(entities, landmarks, areaCode)` → filter via `PoiCandidate` + dedup by `Signature` (increment count, resolve friendly name once) → debounced flush to `config/seen_pois.json`; `All` (locked snapshot), `Flush()`.

**Extended (small):**
- `Core/Campaign/CampaignObjective.cs` — add `ObjectiveCatalog.Covers(in EntityDot)` + `Covers(string landmarkPath)` (reuse `Compiled`). **Unit-tested.**
- `Overlay/Web/CampaignObjectives.cs` — forward `Covers(in EntityDot)` / `Covers(string)` to `_snapshot` (lock-free, like `Rank`).
- `Overlay/Web/ApiServer.cs` — add a `SeenPoiLog`-backed `Func` provider + the `CampaignObjectives` store to the ctor; `GET /api/seen-pois` (entries, each classified covered/uncatalogued + friendly name) and `GET/POST /api/objectives` (list / `{add}` / `{remove}`), POST loopback-gated + a `SanitizeObjective` helper (clamp Label/Category/Priority, validate Rarity/Poi).
- `Overlay/Web/DashboardHtml.cs` — a new **"Director" tab**: ① *Needs cataloguing* (uncatalogued candidates + inline "Add → category + priority"); ② *Catalog* (objectives list — reorder/disable/remove).
- `RadarApp.cs` — construct `_seenPoiLog`; call `_seenPoiLog.Observe(_entities, _landmarks, areaCode)` next to `_modCatalog.Observe` (`~:957`); `_seenPoiLog.Flush()` in `Dispose` (`~:2149`); pass `_seenPoiLog` provider + the existing `_campaign` store into the `ApiServer` ctor.

## 6. The candidate filter (`PoiCandidate.IsCandidate`)

Keep: entities with `Poi == true`; `Category` ∈ { `Npc`, `Chest`, `Transition`, `Object` }; any `Rarity == Unique`; and all tile landmarks (logged separately). **Skip** ordinary `Monster` entities unless they're POI or Unique. Dedup by `Signature` (entity → its `Metadata`; landmark → its `Path`) so 50 identical objects collapse to one entry with a `Count`. This keeps the log to "things that might be objectives," not trash.

## 7. Classification (covered vs uncatalogued)

At the `/api/seen-pois` read (off the hot path), each `SeenPoi` is tagged `covered` by calling `CampaignObjectives.Covers(...)`: entity entries build a minimal synthetic `EntityDot` (Category/Metadata/Poi/Rarity from the entry) and call `Covers(in e)`; landmark entries call `Covers(path)`. Uncatalogued entries sort first — that's the "needs cataloguing" worklist.

## 8. Friendly names

Resolve once at observe-time (or render-time): entities via `EntityNameResolver.Shared.ResolveOrShorten(metadata)`, landmarks via `CuratedName ?? Name`, zone via `ZoneGuide.Shared.FriendlyName(firstZone)`. So the list reads "Fossilised Memorial (Support Gem) — Mausoleum", not a raw `Metadata/…` path.

## 9. Compliance & sync-safety

Read-only throughout (entity reads + local JSON writes, same as `ModCatalog`/`WatchedEntities`/the catalog). Gate stays green. Footprint: 2 new files + a tiny `ObjectiveCatalog` addition + ApiServer/dashboard/RadarApp hooks — listed in `docs/upstream-merge.md`. No identifying data in any payload (I3).

## 10. Testing

- `PoiCandidate.IsCandidate` — pure xUnit: POI/landmark/notable-object/Unique kept; ordinary monsters skipped; `Signature` dedups identical entries.
- `ObjectiveCatalog.Covers` — pure xUnit: returns true iff an enabled objective matches (entity metadata/category/poi/rarity; landmark path); entity objectives don't cross-match landmarks and vice-versa.
- `SeenPoiLog` accumulate/dedup/flush and the dashboard/endpoints → manual release-checklist item (needs the overlay running).

## 11. Risks / open

- **Coverage is only as good as what you log + classify.** Seasonal content shifts each league — the seen-log + "needs cataloguing" loop is what keeps it current (that's the point).
- **Synthetic `EntityDot` for classification** must set the fields the matcher reads (Category, Metadata, Poi, Rarity); other fields default. Off the hot path, so cost is irrelevant.
- **`seen_pois.json` growth** — bounded by distinct candidate signatures (dedup + the filter keep it modest); flush is debounced like `ModCatalog`.

## 12. Out of scope (v1)

- **In-overlay "new POI" badge** — deferred; v1 surfaces candidates in the dashboard only (no renderer changes).
- **Auto-suggesting category/priority** — no; the tool pre-fills a match term, you assign category + priority (I5).
- The other roadmap pieces (richer priority tiers; quest-aware cross-zone guidance) build on the coverage this produces — separate specs.
