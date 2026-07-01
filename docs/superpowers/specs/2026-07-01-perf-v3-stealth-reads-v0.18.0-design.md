# v0.18.0 — Performance v3: Stealth Reads — Design / Spec

**Date:** 2026-07-01
**Branch:** `feat/v0.18.0-stealth-reads` (created at implementation time)
**Status:** Design approved (Ryan: "go" — invisible-only aggressiveness, idle-slowdown not full-pause). Ships after v0.17.0 Remote Views.

## Goal

**Read the game's memory as little as possible while retaining full functionality.** Fewer `ReadProcessMemory` calls/second = smaller detection surface (the stealth mission) and lower CPU. Success is measured by the existing read counter (`MemoryReader._readCount` → `ComputeRpmPerSec`, surfaced as `rpmPerSec` in `/state`): **lower rpmPerSec, zero user-visible change.**

## Compliance invariant (non-negotiable)

Still strictly read-only. This release *removes* reads; it never adds writes/input/pricing. `compliance-gate.ps1` + scrub stay green. README badge stays `0.5.4`. Version → `0.18.0`.

## The guardrail — what "full functionality" means (approved: invisible-only)

A reduction is allowed **only** if it produces **no user-visible change**:
- **Invisible cuts** — don't read data no *enabled* feature consumes, and don't read expensive components for entities that will not be drawn. The user sees identical output because the removed reads fed nothing on screen.
- **Imperceptible refresh** — relax the *rate* of a read only where the underlying value changes slower than the old rate could show (already the pattern for rarity/reaction ~1 s cache). Never drop the refresh of **on-screen dynamic data** (visible HP bars, player vitals, camera) below what looks smooth (~30 Hz floor for anything animated).
- **Idle-slowdown, not full-pause** — when the game is unfocused / minimized / loading / in a safe town-or-hideout, throttle the heavy walk way down, but keep a heartbeat so OBS/Discord/`/map` viewers still get fresh-enough data. Never fully stop (a frozen stream/minimap is a visible regression).

If a candidate reduction would change anything the user can see or use, it is **out of scope** for this release.

## Today's read profile (two cadences)

- **World tick (~30 Hz, `WorldLoop`/`WorldTick`, reader `_live`):** entity std::map walk; per-entity components (rarity, reaction, HP/mana/ES, world position, mods); terrain (per-zone, cached); landmarks; item labels; atlas; nav/route.
- **Render tick (~60+ Hz, `Tick`, reader `_liveRender`):** player vitals; camera matrix; map UI; **per-visible-mob live HP-bar/nameplate position reads** (`TryLiveBarAt`).

`reads/sec ≈ worldReads×30 + renderReads×60`. The render-thread per-mob live reads are the dominant multiplier on a busy screen; the world-walk per-entity component reads are the dominant baseline.

## The reduction levers (priority order — the audit ranks concrete sites within each)

1. **Feature-gating (biggest win).** Reads must scale with *enabled* features:
   - HP bars off → skip Life-component reads in **both** the world walk and the render live-pos path.
   - Affix nameplates off → skip mod reads. Item labels off → skip item reads. Atlas closed → skip atlas reads. Preload off → skip file-list reads (already off by default).
   - Every expensive per-entity read gets a `if (featureEnabled)` gate.
2. **Cull-before-read.** Read the cheap discriminator first (position + category/reaction), then read the expensive components (HP, mods, affixes) **only for entities that pass the draw filter** (in radar radius, not hidden by `DisplayRules`, on-screen for camera-projected features). No component reads for dots that will never render.
3. **Idle-slowdown.** Detect unfocused / minimized / loading / town-hideout and drop the world-tick cadence (e.g. 30 Hz → a few Hz) + skip monster-centric reads where there are no monsters. Keep a low-rate heartbeat for API/stream consumers.
4. **Redundant-read elimination.** Where the world walk and the render thread both read the same address (e.g. a mob's world position feeding both a dot and its HP bar), publish once and reuse rather than reading twice.
5. **Refresh-rate relaxation on slow data.** Extend the ~1 s slow-refresh discipline (rarity/reaction) to any field that changes slower than it's read (e.g. area/character metadata, map-UI zoom when the map is closed).
6. **Batching.** Collapse multiple small field reads into one struct read where the layout is contiguous (fewer RPM calls for the same bytes). The component cache already does some of this; the audit finds remaining hot spots.

## Method — multi-agent read audit (the discovery phase)

Because the read layer is large (`Poe2Live.cs` especially, plus `Poe2Atlas`, the readers, `RadarApp` render/world ticks), the concrete reduction list is produced by a **multi-agent audit workflow**, not guessed:

1. **Inventory (fan-out):** agents catalog **every** read call-site across the read layer — for each: file:line · what it reads · cadence (per-render-frame / per-world-tick / per-zone / on-demand) · which feature consumes it · cached? · already gated? · rough reads-per-second contribution.
2. **Synthesize (rank):** one pass merges the inventory and ranks candidate reductions by `reads-saved × safety`, mapping each to a lever above, with the functionality-preservation argument and the risk. Anything that can't be cut invisibly is dropped.
3. **Output:** a prioritized reduction list → becomes the implementation plan's task set. Each task cites the exact call-site(s) it changes.

## Measurement (definition of done)

- Instrument a repeatable **before/after `rpmPerSec`** comparison in two scenarios, read from `/state`:
  - **Stress:** busy map, all features ON — proves the reductions help the heaviest case.
  - **Default:** default install (perf-features OFF) — proves the baseline footprint drops (the stealth number that matters most).
- Record numbers in the plan/release notes. Target: a **meaningful reduction** in both, with the default case gutted (feature-gating means an idle default install reads far less).
- **Functionality regression check:** every touched feature smoke-tested live — dots, HP bars, nameplates, arrows, atlas, nav, preload, OBS/Discord/`/map` all behave identically; only `rpmPerSec` moves.

## Architecture / boundaries

- **Core (`POE2Radar.Core`):** `Game/Poe2Live.cs` (the bulk — gate + cull + batch the entity/component/terrain reads), `Game/Poe2Atlas.cs`, the reader stacks. Reductions are internal; the public read API (returned snapshots) is unchanged so consumers don't move.
- **Overlay (`POE2Radar.Overlay`):** `RadarApp.cs` (world/render tick — feature-gate which reads run based on enabled `RadarSettings`; idle-slowdown cadence), possibly `OverlayRenderer` (draw-filter feeding the cull-before-read decision back to the read layer via the spec lists it already builds).
- **Feature flags read from `RadarSettings`** decide which reads run — the settings already exist per feature; this wires them into the read path so "off" means "not read," not just "not drawn."
- No new offsets, no new public types beyond small internal gating helpers. Existing tests must stay green; new unit tests cover pure gating/cull decision helpers where extractable.

## Data flow (unchanged externally)
The published `WorldSnapshot` / `RadarState` / `AtlasRender` shapes are **unchanged** — only *how much is read to produce them* changes. Render/API/stream consumers see the same data, just sourced with fewer RPM calls (and idle-throttled when nobody's looking at the game).

## Error handling
- Gating must be **fail-safe toward correctness**: if the enabled-state is ambiguous, read (a spurious read is cheaper than a missing feature). Cull-before-read must never drop an entity that *would* be drawn — the draw filter is the single source of truth, reused, not re-derived.
- Idle-detection false-positive (throttling while actually playing) is prevented by keying on focus + in-game + area-kind, and the heartbeat guarantees data never goes fully stale.

## Out of scope (YAGNI)
- Aggressive/visible tradeoffs (sub-30 Hz visible HP bars, full event-driven reads, full-pause on unfocus) — explicitly declined (invisible-only).
- New features (this is pure read-reduction).
- Changing the published snapshot/API shapes.
- Any new offset discovery.

## Audit results — concrete reduction plan (produced 2026-07-01)

The 5-region + synthesis audit workflow ran over the read layer. **Full ranked findings (17 reductions with per-site implementation sketches):** [`2026-07-01-perf-v3-audit-synthesis.md`](2026-07-01-perf-v3-audit-synthesis.md).

**Headline baseline finding:** the dominant read cost by 10–100× is **`Poe2Atlas.ReadCanvasNodes` re-walking ~20,000 reads EVERY WorldTick (~30 Hz) while the Atlas is open — even when the freeze-signature is about to discard the result** (the sig is computed *after* the walk). ~600,000 reads/sec of pure waste while the atlas is open + static. Next: the render thread runs all reads at 60 Hz even when PoE2 is unfocused and the overlay draws nothing (~7,680 reads/sec wasted while tabbed out). Then feature-ungated reads that always run regardless of whether their feature is enabled.

**The 11 SDD tasks (sequenced, invisible-safety first, biggest+safest first):**
- **SR-1 (huge):** Static-atlas fast path — compute the freeze pre-signature BEFORE `ReadNodes()`; skip the ~20k-read canvas walk when unchanged. ~600k reads/sec while atlas open+static. (`RadarApp.UpdateAtlas`, `Poe2Atlas.ReadNodes/ReadCanvasNodes`)
- **SR-2 (high):** Unfocused idle-slowdown — gate the render `if (inGame)` read block behind `realActive || _overlayHadContent` (one clear-frame, then skip). ~7,680 reads/sec while tabbed out. (`RadarApp.Tick`)
- **SR-3:** Feature-gate `ReadMods` on `AffixNameplates.Enabled || HasModFilter` (Poe2Live `EnableModReads`).
- **SR-4:** Feature-gate `ReadItemIdentity` on `GroundItems.Enabled || rule.AppliesToItems` (Poe2Live `EnableItemIdentityReads`).
- **SR-5 (quick-wins batch):** (a) render `AreaHash` → use `snap.AreaHash`; (b) `PlayerGrid`+`PlayerWorld` single-read; (c) gate `CameraMatrix` on HP-bars/nameplates/item-labels enabled; (d) drop `EntityCategory.Player` from the `ReadHp` condition.
- **SR-6 (quick-wins batch):** (a) permanent-cache `PlayerName`; (b) slow-refresh `PlayerLevel` to ~0.2 Hz; (c) gate/slow-refresh `PlayerVitals` on `EnableAutoFlask || ShowVitalsHud` (10 Hz HUD-only; full rate when auto-flask on).
- **SR-7:** Atlas per-node feature gates — `iconType` child-walk on `AtlasShowContentIcons`; status reads on `AtlasAutoRoute || AtlasHideCompleted || AtlasHideAccessible`. ~330k + ~99k reads/sec when those atlas features off.
- **SR-8:** POI `CompletedState` one-way cache in `ReadIcon` (mirrors `ReadChestOpened`) + 10-tick slow-refresh for live POIs.
- **SR-9:** `ReadMonolith` static-data cache — live-read only the `Collected` flag (reuses SR-8). ~1,260 reads/sec when Monoliths on.
- **SR-10:** Cull off-screen atlas marks before the render-thread `TryRelPos` loop. ~6,000 reads/sec while atlas open.
- **SR-11:** Slow-refresh `CurrentNodeGrid` to ~1 Hz (after SR-1).

Every task's deliverable is verified by the existing `rpmPerSec` (`/state`) counter dropping in the relevant scenario, with the touched feature smoke-tested identical. Auto-flask vitals stay full-rate when enabled (safety-critical). All invisible/imperceptible — no user-visible change.

## Version
Ships as **v0.18.0 — Performance v3 (Stealth Reads)**. README badge stays `0.5.4`. SDD flow. Discord announcement drafted at release.
