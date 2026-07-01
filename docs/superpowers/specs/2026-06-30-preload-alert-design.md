# v0.14.0 — Preload Alert — Design / Spec

**Date:** 2026-06-30
**Branch:** `feat/v0.14.0-preload-alert`
**Status:** Reader validated live (3 probe runs); design approved (Ryan: "Build diff-based + test live") — spec for implementation

## Goal

On zone entry, preview the zone's notable content (pinnacle/unique bosses, league encounters, valuable chests) by reading the **loaded-files list** the game populates for the area and matching it against a curated **path→content catalog** — the most-loved ExileApi feature POE2GPS lacks. **Opt-in, default OFF, experimental** (a "Preload Alert" the community can help tune).

## Compliance invariant (non-negotiable)

Strictly **read-only**: `OpenProcess` (read) + `ReadProcessMemory` only — the AOB scan reads executable sections (as the GameState scan already does); the loaded-files walk reads heap. **No** writes/injection, **no** input emission, **no** pricing/reward-VALUES (we surface *content types* — "there's a Breach here" — never "worth X divine"; no poe.ninja/trade). Default-off opt-in. `scripts/compliance-gate.ps1` + scrub stay green. README badge stays `0.5.4`.

## Validated reader (live, 3 sessions — do not re-discover unless a patch breaks it)

Ported from GameHelper2 (same upstream as our existing offsets). Confirmed via `--preload` probe:

- **File Root AOB** (already committed to `AobPatterns.FileRootRefs`): `48 8B 0D ^ ?? ?? ?? ?? E8 ?? ?? ?? ?? E8` (DispOffset 3, InstrLen 7). Resolves to **exactly 1 stable slot** across PIDs/sessions. Resolve once at bootstrap (like `GameStateRefs`), cache the slot; deref each scan → FileRoot object.
- **Walk:** FileRoot → **16 buckets**, stride **0x38**; each bucket is a `StdVector{First,Last,End}` at bucket+0x00. Node stride **0x18** (`FilesPointerStructure`): `FilesPointer @ +0x08` → `FileInfoValueStruct { Name StdWString @ +0x08, AreaChangeCount int @ +0x40 }`. `ReadStdWString` is SSO-aware (len @ str+0x10, inline if <8 else heap ptr). Split the path on `'@'` (keep `[0]`). Full walk ≈ **21,871 paths**.
- **Reuse:** `AobScanner.ScanForResolvedAddresses`, `MemoryReader.TryReadStruct`/`ReadStdWString`-equivalent, `StdVector`. No new primitives.

## The filtering design (the core decision)

The probe revealed the loaded-files list is **broad, not zone-specific**: `+0x40` was uniformly `2` (not discriminating), *all* leagues' assets were resident, and Art/Models + Metadata/Effects paths are broadly preloaded (all likely a **town/hub** snapshot). So naive path→catalog matching over-alerts. Two-layer filter:

1. **Entity-metadata gate.** Only consider paths under content-bearing roots: `metadata/monsters/`, `metadata/chests/`, `metadata/terrain/leagues/`, `metadata/miscellaneousobjects/league*`. Ignore `art/`, `metadata/effects/`, `data/` (noise). Match these against the catalog (lowercased substring rules).

2. **Zone-frequency common-subtraction (the "diff").** Maintain a persisted `Dictionary<string,int> _pathZoneHits` + `int _zonesObserved`. Each NEW zone (AreaHash change), after the scan, increment `_zonesObserved` and bump the hit-count of every catalog-matched path seen. A path is **"common noise"** when `hits / zonesObserved ≥ CommonThreshold` (default `0.6`) once `zonesObserved ≥ WarmupZones` (default `4`). **Alerts = this zone's catalog matches that are NOT common.** Self-tunes: content that's actually per-zone (a specific boss/breach) stays rare → alerts; always-loaded assets saturate → suppressed. Persisted to config so it's smart after a few sessions. (Rationale: robust to the uniform `+0x40`; no dependency on resolving a per-zone counter.)

**Diagnostic mode** (opt-in, for the live-tuning phase): the overlay/API expose, per zone, *all* catalog matches with their `hits/zonesObserved` frequency + the raw new-vs-common split, so Ryan and the community can see signal vs noise and tune `CommonThreshold`.

## Catalog

Embedded `preload_catalog.json` (Core), seeded from the 69-rule draft (`scratchpad/preload_catalog_draft.json`) + refined with the probe's real paths. Each rule: `{ match (lowercase substring), label, tier (pinnacle|high|mechanic|interactable), category, color }`. Tiers drive colour + audio. Confirmed real content paths from the probe to include: `metadata/chests/blightchests/`, `metadata/chests/incursionatzirileadupchests`, `metadata/chests/leagueheist/`, `metadata/chests/delvechests/`, `metadata/monsters/leagueexpeditionnew/expedition2/`, plus the 69 curated boss/exile/mechanic rules. Regeneratable/extendable; unmatched content-root paths can be surfaced in diagnostic mode to grow the catalog.

## Overlay UX

- **Opt-in, default OFF.** Settings card "Preload Alert" (ships collapsed), clearly marked *experimental*.
- **Zone-entry panel** (mirrors the existing Zone Summary panel idiom): on zone load, list detected content — tier-coloured lines (e.g. 🔴 *Xesht — Breachlord*, 🟡 *Expedition*), grouped by tier, shown for a few seconds or until the next zone. Anchor/offset configurable.
- **Audio cue** (optional, reuses the existing `AudioCue` system): a tone when `≥ AudioTier` content is detected (default: pinnacle only).
- **Diagnostic overlay/API** (optional): full match list + frequencies for tuning.

## Architecture / boundaries

- **Core:** `Game/Poe2LoadedFiles.cs` (new) — resolve/cache FileRoot slot + walk → `IReadOnlySet<string>` of lowercased paths (once per call). `Game/PreloadCatalog.cs` (new) — load embedded `preload_catalog.json`, `Match(path) → CatalogHit?`. `Game/preload_catalog.json` (new embedded). `AobPatterns.FileRootRefs` (done). A small `LoadedFilesOffsets` block in `Poe2Offsets.cs`.
- **Overlay:** `RadarApp` world loop — on AreaHash change, run ONE loaded-files scan (heavy; world thread, off the render/hot path), apply the entity-gate + catalog + frequency-common filter, publish an immutable `PreloadFrame` (list of `PreloadHit`) into the snapshot. Render thread draws the panel from it. `Config/RadarSettings.PreloadAlert` (new settings object). `Web/ApiServer` (`/state` preload block + patch cases + diagnostic). `Web/DashboardHtml` (settings card + diagnostic view). `Overlay/OverlayRenderer.DrawPreloadPanel`. Persist `_pathZoneHits`/`_zonesObserved` (in config or a sidecar).
- **Performance:** the ~21k-path walk runs **once per zone** on the world thread (never per-frame). Bulk-read each bucket's node vector where possible. Acceptable at zone cadence (~1–3 min).

## Testing

- **Core unit tests:** `PreloadCatalog` load + `Match` (known path → expected tier/label; non-content path → null); the frequency-common filter logic (a path saturating over N zones flips to common; a rare path stays alerting) as a pure testable unit.
- **Build + live:** 0/0 build + full suite + compliance/scrub + per-task & final review, then **live tuning in real maps** (the diagnostic mode is the acceptance tool — Ryan runs maps, we watch signal-vs-noise, tune `CommonThreshold`, grow the catalog).

## Out of scope (YAGNI)

- Reward/currency VALUES or pricing (compliance).
- Per-file `AreaChangeCount` filtering (it didn't discriminate; the frequency-common filter replaces it).
- Auto-navigation to preloaded content (future; v0.14 just alerts).

## Version

Ships as **v0.14.0** (experimental Preload Alert). README badge stays `0.5.4`. SDD flow. The reader (Core offsets + AOB) is committed to Poe2Offsets/AobPatterns and marked ✓ validated 2026-06-30.
