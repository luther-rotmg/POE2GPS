# Entity Atlas — Design Spec

- **Date:** 2026-06-21
- **Status:** Approved 2026-06-21
- **Topic:** A repeatable capture → classify → publish workflow that grows comprehensive PoE2 entity
  coverage for the GPS — every entity gets a friendly name (radar legibility) and the notable ones get
  classified as Director objectives. Community-growable, fully read-only. The generalization of the
  Catalog Builder from "Director candidates" to the full entity census.

---

## 1. Summary

PoE2's entities are opaque metadata paths (`Metadata/Monsters/Wraith/WraithSpookyLightning`). The
overlay names them via an embedded table (`entity_names.json`) and the Catalog Builder logs *candidate*
POIs, but two gaps remain: (a) lots of entities have **no friendly name**, so the radar/legend shows a
raw path segment; (b) coverage only grows by hand-editing the baked-in table or by the Director's
narrow candidate filter. The **Entity Atlas** closes both as one in-session loop:

> play → the overlay **logs the full entity census** → the dashboard **"Atlas" tab** shows what's
> **unnamed** and what's **unclassified-but-notable** → you name it (radar updates live) or classify it
> (becomes a Director objective) → export a shareable pack so the dataset grows across the community.

It cannot do the live signature research for you (that needs a running PoE2 client). The designable
deliverable is the **tooling/workflow** that makes "map every entity" a repeatable, ~seconds-per-entry
task. It is **read-only** (reads entity metadata, writes local JSON) and additive/sync-safe — built on
the same hardened accumulator the Catalog Builder uses.

## 2. Goal & non-negotiable invariants

- **I1 — Read-only.** Reads entity metadata; writes only local config JSON. No input-emission or
  process-write APIs. `scripts/compliance-gate.ps1` stays green.
- **I2 — Reuse existing machinery, no duplication.** The accumulator mirrors the hardened `SeenPoiLog`
  exactly; naming reuses `EntityNameResolver`; classification reuses the Director's `CampaignObjectives`
  store; the dashboard tab reuses the just-built Director-tab pattern.
- **I3 — No identifying data.** Dashboard payloads carry no character name (consistent with the
  stealth posture).
- **I4 — Sync-safe footprint.** New isolated files + a tiny `EntityNameResolver` addition + small hooks
  (one `WorldTick` observe, one `Dispose` flush, ApiServer wiring, a dashboard tab), all documented in
  `docs/upstream-merge.md`.
- **I5 — The tool captures + surfaces; the human judges.** It never auto-decides a name, relevance, or
  classification — you do. (Auto-suggested names are explicitly out of scope for v1.)
- **I6 — Carry forward the v0.1.2 perf lessons.** The census accumulator flushes to disk **only on a new
  signature** (never the every-4s storm), runs entirely on the world thread, and is bounded by distinct
  metadata paths.

## 3. Verified integration facts (grounding)

Confirmed by reading `main`:

- **Naming — `EntityNameResolver`** (`Core/Game/EntityNameResolver.cs`): a static `Shared` singleton
  holding one `Dictionary<string,string>` (OrdinalIgnoreCase) loaded once from the embedded
  `entity_names.json` (~301 KB, lower-cased `metadata/path → "Name"`). `Resolve` tries an exact match,
  then progressively drops trailing path segments (so spawn variants resolve to a base entry), and
  strips a trailing `@<level>` annotation. `ResolveOrShorten` falls back to the last path segment when
  unknown — **this fallback is exactly the "unnamed" signal** the Atlas worklist keys on. Used in
  `/entities` (`ApiServer.cs:213`) and `SeenPoiLog`. The singleton is read from multiple threads
  (world/render/API).
- **Census filter — `JunkFilter`** (`Core/Game/JunkFilter.cs:89`): `static bool IsJunk(string metadata)`
  — pure case-insensitive substring match against a curated junk list (FX/`/fx/`, `/audio/`, `/daemon/`,
  `monstermods`, `/clone/`, MTX, attachments, …). Drops the non-entity noise so the census stays real.
- **Match surface — `Poe2Live`**: `EntityDot { Id, Grid, Category, Metadata, Poi, Rarity, … }`;
  `EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }`.
- **Entity source — `RadarApp.WorldTick`**: `_entities = _live.Entities(areaInstance)` (`~:946`) returns
  a **fresh** `List<EntityDot>`; it is then culled **in place** for the local player + user-hidden
  metadata (`RemoveAll`, `~:957`). The Director's `SeenPoiLog` observes the **post-cull** list (so its
  worklist respects hides); the Atlas observes the **pre-cull** list (so naming coverage is complete
  regardless of the user's hide preferences) — see §6.
- **Accumulator template (hardened) — `SeenPoiLog`** (`Overlay/Web/SeenPoiLog.cs`, post-v0.1.2): `_gate`
  lock + `Dictionary` + `Stopwatch _sinceDirty` + split dirty flags (`_dirty` = new signature → arms the
  4 s debounced periodic flush; `_countsDirty` = repeat-sighting drift → persisted only at shutdown
  `Flush()`); `Observe` loops then `MaybeFlush`; `Load`/`Save` JSON round-trips to a `ConfigDir` path.
  Constructed in the `RadarApp` ctor; observed in `WorldTick`; flushed in `Dispose`.
- **Classification store — `CampaignObjectives`** (`Overlay/Web/CampaignObjectives.cs`): `All`,
  `Add(CampaignObjective)` (upsert by Id), `Remove(string id)`, `Covers(...)`, under `_gate` + volatile
  `_snapshot`. Already wired into `ApiServer` (Catalog Builder) and the Director. "Classify" = `Add`.
- **Dashboard tab pattern** (`DashboardHtml.cs`, the Director tab just shipped): a
  `<button class="tab" data-tab="X">`, a `<section class="view" data-view="X" hidden>`, and one
  `if(activeTab==='X') loadX();` line in the tab-switch closure. Read helpers `$`, `$$`, `getJSON`,
  `esc`, `cssEsc` already exist.
- **Endpoint patterns** (`ApiServer.cs`): READ via an injected `Func` (`/api/seen-pois`); WRITE via a
  GET+POST case that loopback-gates (`IsLoopbackHost`), `ReadBody` + `JsonDocument.Parse`, then mutates a
  store. JSON serialized camelCase via the shared `Json` options.
- **Override-file precedent**: the `icons/` folder materializes built-in SVGs next to the exe and any
  `*.svg` dropped there overrides a built-in. The Atlas's name-override layer mirrors this idea at the
  data level (`config/entity_names_user.json` overrides the embedded table).

## 4. Architecture & data flow

```
world tick ─▶ EntityAtlasLog.Observe(rawEntities, areaCode)        (accumulator; mirrors hardened SeenPoiLog)
                 │  FULL census of the pre-cull entity list; skips Player + JunkFilter.IsJunk noise;
                 │  dedup by metadata signature; records category, rarity, firstZone, count, firstSeenUtc;
                 │  flush ONLY on a new signature → config/entity_atlas.json
                 ▼
   dashboard "Atlas" tab → GET /api/atlas ──▶ each census entry tagged:
        named?   = EntityNameResolver.Resolve(metadata) != null   (exact/prefix hit, override-aware)
        covered? = CampaignObjectives.Covers(synthetic EntityDot)  (a Director objective matches it)
                 │
                 ├─ "Needs a name"  (named? == false): type a friendly name
                 │     POST /api/atlas/name {metadata, name}
                 │       → EntityNameResolver user-override layer (LIVE) + config/entity_names_user.json
                 │       → radar/legend/Atlas show the new name immediately, no rebuild
                 │
                 └─ "Notable, uncatalogued"  (covered? == false, notable category): one-click classify
                       POST /api/objectives {add:{…}}   → CampaignObjectives.Add  (reuses the Director; already wired)
                 ▼
   Export → GET /api/atlas/export  ──▶ atlas-pack.json { names:{…}, objectives:[…] }
   Import → POST /api/atlas/import ──▶ merge a shared pack (names into the override layer; objectives upsert)
```

## 5. Components / file structure

**New, isolated:**
- `Overlay/Web/EntityAtlasLog.cs` — the census accumulator (I/O), **mirroring hardened `SeenPoiLog`**:
  `Observe(IReadOnlyList<EntityDot> entities, string areaCode)` → for each non-Player, non-junk entity,
  dedup by metadata signature (increment count) or insert a new `AtlasEntry` → split-dirty debounced
  flush to `config/entity_atlas.json`; `All` (locked snapshot), `Flush()`. The `AtlasEntry` record
  `{ Metadata, Category, Rarity, FirstZone, Count, FirstSeenUtc, LastSeenUtc }` lives in
  `Core/Campaign/` (pure, unit-testable) next to `SeenPoi`.

**Extended (small):**
- `Core/Game/EntityNameResolver.cs` — **add a live user-override layer**: a `volatile` reference to an
  immutable override `Dictionary` consulted (exact-then-prefix, same as the embedded table) **before**
  the embedded table in `Resolve`; a thread-safe `SetUserOverrides(IReadOnlyDictionary<string,string>)`
  that atomically swaps the reference (lock-free reads on the hot path; rare writes on the user action).
  Loading/owning `config/entity_names_user.json` belongs to the Atlas store (Overlay layer), which calls
  `SetUserOverrides` at startup and after each edit — `Core` stays free of file/Overlay concerns.
- `Overlay/Web/EntityNameStore.cs` — owns `config/entity_names_user.json` (load → `SetUserOverrides`;
  `Add(metadata, name)` → update map, swap overrides, debounced save; `All` for export). Thin; the
  resolver does the lookup, this does the persistence + wiring (separation of concerns).
- `Overlay/Web/ApiServer.cs` — inject the `EntityAtlasLog` provider + the `EntityNameStore`; add
  `GET /api/atlas` (census entries, each tagged named?/covered? + friendly name), `POST /api/atlas/name`
  (loopback-gated; `{metadata, name}` sanitized), `GET /api/atlas/export`, `POST /api/atlas/import`
  (loopback-gated; merges names + objectives). Reuse `IsLoopbackHost`/`ReadBody`/the camelCase `Json`.
- `Overlay/Web/DashboardHtml.cs` — a new **"Atlas" tab**: ① *Needs a name* (unnamed census entries,
  inline name input + save); ② *Notable, uncatalogued* (notable entries no objective covers — one-click
  classify with category + priority, reusing the Director's add); ③ Export / Import buttons.
- `RadarApp.cs` — construct `_entityAtlas` + `_entityNameStore` (the latter wires the override layer at
  startup); call `_entityAtlas.Observe(_entities, areaCode)` **right after `_entities` is read and before
  the user-hidden cull** (§6); `_entityAtlas.Flush()` + `_entityNameStore.Flush()` in `Dispose`; pass the
  Atlas provider + name store into the `ApiServer` ctor.

## 6. The census filter & observe point

Capture every distinct entity **except**: `EntityCategory.Player` (that's "you"/party — not atlas
content) and anything `JunkFilter.IsJunk(metadata)` flags (FX/audio/daemon/MTX/clone noise). Dedup by
metadata signature so thousands of identical monsters collapse to one entry with a `Count`.

**Observe the pre-cull list.** The Atlas's `Observe` runs on the freshly-read `_entities` **before** the
local-player + user-hidden `RemoveAll` (`RadarApp.cs ~:957`), so the census is complete even for entity
types the user has hidden from their radar (hiding a dot shouldn't erase it from the name database). The
local player is excluded by the Player-category skip above, not by the cull. (This is the one
intentional divergence from `SeenPoiLog`, which observes the post-cull list because the Director worklist
*should* respect hides.)

## 7. Classification (named? / covered?)

At the `/api/atlas` read (off the hot path), each census entry is tagged:
- **named?** — `EntityNameResolver.Resolve(metadata) != null` (override-aware exact/prefix hit). False ⇒
  it shows up under *Needs a name*.
- **covered?** — build a minimal synthetic `EntityDot` (Category/Metadata/Rarity/Poi from the entry) and
  call `CampaignObjectives.Covers(in e)` (the Catalog Builder's matcher). An entry is **notable** iff
  `PoiCandidate.IsCandidate(in e)` is true (the exact Catalog-Builder notion: POI / Unique / Npc / Chest /
  Transition / Object — *not* ordinary monsters). `covered? == false` **and** notable ⇒ it shows up under
  *Notable, uncatalogued*. (Reusing `IsCandidate` keeps "notable" defined in exactly one place — I2.)

"Classify" reuses the Director's one-click add (`POST /api/objectives {add:{…}}`) with a richer
**recommended** category vocabulary — `League`, `PermanentUpgrade`, `GemSource`, `Boss`, `SideZone`,
`SideBoss`, `Other` — surfaced as the dropdown options. No schema change; the vocabulary is a UI
convention (the store already accepts any sanitized category string).

## 8. Friendly names — the live override layer

`config/entity_names_user.json` is a flat `metadata/path → "Name"` map (same shape/keys as the embedded
table). `EntityNameStore` loads it at startup and calls `EntityNameResolver.SetUserOverrides(...)`;
naming an entry updates the map, atomically swaps the resolver's override reference (so every
name-resolving path — radar legend, `/entities`, the Atlas itself — reflects it **immediately**), and
debounced-saves the file. Precedence: **user override → embedded table → shortened fallback**. Your
overrides file is exactly what you export to contribute upstream into the embedded `entity_names.json`
each patch (a dev-time merge via `resources/poe2-data`, out of band).

## 9. Export / import (community)

- **Export** (`GET /api/atlas/export`) → `atlas-pack.json` = `{ names: {metadata: name, …}, objectives:
  [CampaignObjective, …] }` (names from `EntityNameStore.All`; objectives from `CampaignObjectives.All`).
  No identifying data.
- **Import** (`POST /api/atlas/import`, loopback-gated) → merges a pack: names go through
  `EntityNameStore.Add` (so they layer + persist + go live); objectives go through
  `CampaignObjectives.Add` (upsert by Id). Last-write-wins on key conflicts; import is additive (never
  deletes). This is what makes the dataset community-growable beyond one player's session.

## 10. Compliance & sync-safety

Read-only throughout (entity-metadata reads + local JSON writes, same as `ModCatalog`/`SeenPoiLog`). Gate
stays green. Footprint: 2 new Overlay files + 1 pure `Core/Campaign` record + a small
`EntityNameResolver` override addition + ApiServer/dashboard/RadarApp hooks — listed in
`docs/upstream-merge.md`. No identifying data in any payload (I3). The override layer is purely additive
over the embedded table, so an upstream merge that updates `entity_names.json` never conflicts with a
user's overrides.

## 11. Testing

- `EntityAtlasLog` census filter — pure xUnit (in `Core/Campaign`): Player skipped; `JunkFilter` noise
  skipped; monsters/NPCs/objects/chests/transitions kept; identical metadata dedups to one entry with a
  `Count`.
- `EntityNameResolver` override precedence — pure xUnit: a user override beats the embedded table for the
  same key; the prefix-fallback still applies under the override; `SetUserOverrides` swap is observed by a
  subsequent `Resolve`; an empty/blank override doesn't shadow a real embedded name.
- `EntityNameStore` / `EntityAtlasLog` accumulate/flush, the endpoints, the Atlas tab, and export/import
  round-trip → manual release-checklist item (needs the overlay running).

## 12. Out of scope (v1)

- **In-overlay "inspect entity" hotkey** — capture/inspect the entity under the cursor in-game. Deferred;
  v1 is dashboard-driven (the census already captures everything passively).
- **Patch/league tagging** — `AtlasEntry` records `FirstSeenUtc` (cheap, no API), but a structured
  patch/league dimension + filters are deferred.
- **Auto-suggested names** — mining the metadata path / game-data tables to pre-fill names. Deferred;
  v1 you type the name (I5). `ResolveOrShorten`'s last-segment shorthand is shown as a non-binding hint.
- **Writing back into the embedded `entity_names.json`** at runtime — never; the embedded table is
  build-time reference data, grown out-of-band from exported packs.

## 13. Risks / open

- **Census size.** Full coverage is larger than the Catalog Builder's candidate set (thousands of
  distinct metadata paths across a campaign). Bounded by *distinct* paths (dedup + JunkFilter keep it to
  low thousands ⇒ ~1–2 MB JSON), flush-on-new-signature keeps writes rare — same envelope as the audited
  `SeenPoiLog`. Worst case is a modest file, never unbounded.
- **Resolver override thread-safety.** `Resolve` runs on world/render/API threads; the override must be a
  `volatile` immutable reference swapped atomically (lock-free reads). Writes are rare (user actions).
- **Synthetic `EntityDot` for `covered?`** must set the fields the matcher reads (Category, Metadata,
  Rarity); others default. Off the hot path, cost irrelevant (same as the Catalog Builder).
- **Name-quality drift.** Community import is last-write-wins; a bad imported name can shadow a good one.
  Mitigated by: overrides are local + editable, import is additive (re-naming fixes it), and the embedded
  table is the trusted floor.
