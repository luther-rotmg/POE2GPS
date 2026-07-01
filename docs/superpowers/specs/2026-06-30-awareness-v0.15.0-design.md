# v0.15.0 — Situational Awareness — Design / Spec

**Date:** 2026-06-30
**Branch:** `feat/v0.15.0-awareness`
**Status:** Design approved (Ryan) — spec for implementation. Two features shipping together; zero new offsets.

## Goal

Two low-risk, high-value awareness features built entirely on data the overlay already reads:
1. **Off-screen entity arrows + alert rules** — edge arrows pointing to notable off-screen entities.
2. **Session HUD v2** — kill counter (observed), maps/hr, XP-efficiency.

Both reuse existing systems (DisplayRules, camera projection, edge-arrow renderer, SessionTracker). **No new offsets, no new memory reads.**

## Compliance invariant (non-negotiable)

Strictly **read-only** (existing RPM reads only — no new reads). No input emission, no process writes, no pricing/reward-values. Opt-in where it adds screen elements. `compliance-gate.ps1` + scrub stay green. README badge stays `0.5.4`. Version → `0.15.0`.

---

## Feature 1 — Off-screen entity arrows + alert rules

### Behavior
When an entity matched by an **arrow-enabled display rule** (e.g. Unique, Boss) is currently **off the visible game window**, draw an arrow at the window edge pointing toward it, in the rule's colour, optionally labelled (rule name) + distance. On-screen matches draw nothing extra (already visible). The "alert-rule system" **is** the existing `DisplayRule` engine, extended — not a new parallel system.

### Architecture (mirrors HP bars / affix nameplates)
- **World thread** (`RadarApp.WorldTick`): for each live entity whose matched `DisplayRule.OffScreenArrow == true`, emit an `EntityArrowTarget(WorldPos, Color, Label)` into a frame published in `WorldSnapshot` (same lock-free volatile-swap idiom as `_hpFrame`/`_affixFrame`). Gated on `EntityArrows.Enabled`.
- **Render thread** (`Tick`): project each target's world pos via the camera matrix (`Poe2Live.CameraMatrix`, same as HP bars). If the projected point is **outside** the window bounds, draw an edge arrow via a generalized `DrawEdgeArrow` (extract/rename the existing `OverlayRenderer.DrawAtlasArrow`, which already computes the border-intersection + arrowhead + optional label). Skip on-screen targets. Respect the zone-load guard (`snap.AreaHash == liveAreaHash`) like all snapshot data.
- **Cap + declutter:** draw at most `EntityArrows.MaxArrows` (nearest-first); skip targets closer than `MinDistanceCells` to the edge (avoid edge spam).

### Rule extension
- Add `public bool OffScreenArrow { get; set; }` to `DisplayRule` (`Web/DisplayRules.cs`).
- Dashboard rule editor gains an "off-screen arrow" checkbox per rule.
- **One-time seed:** enable `OffScreenArrow` on the default **Unique** + **Boss/Citadel-class** rules only (high-signal, low-noise), gated by a `RadarSettings` `EntityArrowsSeeded` flag so a user who turns it off keeps it off.

### Settings (`RadarSettings.EntityArrows` — new object)
- `Enabled` (master, default **true**), `Size` (px), `ShowLabel` (bool), `ShowDistance` (bool), `MaxArrows` (int, default ~12, clamp 1–40), `MinEdgeDistancePx` (int, avoids edge spam).
- `EntityArrowsSeeded` (one-time guard).

### Testing
- Core unit test: an **edge-intersection helper** `EdgeArrow.BorderPoint(screenX, screenY, W, H, margin) → (x, y, angle)` (pure) — the math that maps an off-screen point to its window-border arrow position. Table-tested for the 8 directions + corners.
- Live smoke: arrows point correctly toward off-screen uniques/bosses; on-screen ones don't; cap + min-distance behave.

---

## Feature 2 — Session HUD v2

Three metrics added to the existing Session HUD, each an independent toggle (like the current metrics), all extending `SessionTracker`/`SessionStats` (Core).

### 2a. XP-efficiency
`player level − area level` as a signed readout (e.g. `+3`, `−5`), colour-coded (over-levelled vs under-levelled). Both values already read each tick. Pure display; feed the two ints into `SessionStats`.

### 2b. Maps/hr
Count **map-zone entries per hour**. On each zone change (existing signal), if the new area is a **map** (not town/hideout — reuse the existing town/hideout classifier the Session HUD pace already uses via `ExcludeTownsFromPace`), increment a map-entry counter. `MapsPerHour = mapEntries / sessionHours`. Labelled "Maps/hr". (Map *entries*, not *completions* — completion isn't cleanly detectable without more RE.)

### 2c. Kills (observed), by rarity
A pure `KillTracker` (Core) counts monster **HP → 0 transitions** by rarity:
- Each world tick, the world thread feeds observed monsters `(address, rarity, hpFraction)` — HP is already read for HP bars. (Feed only monsters we already have HP for; no new reads.)
- `KillTracker.Observe(address, rarity, hp)`: track last-seen HP per monster address; when HP transitions from `>0` to `≤0`, increment the rarity counter (Normal/Magic/Rare/Unique) and drop the address. Evict addresses not seen for N ticks (zone change / out-of-range) WITHOUT counting them (only HP→0 counts).
- Displayed as `Kills: 142 (R 8 · U 1)` or per-rarity lines. **Labelled "observed"** — it counts kills we witnessed (near you); far off-screen / pre-cull kills read low. This is the honest no-offset trade (the alternative — counting vanished entities — over-counts badly).
- `KillTracker` is fully unit-testable (feed HP sequences → assert counts; assert eviction doesn't count as a kill; assert HP→0 counts once).

### Reset
All three tie into the existing **Ctrl+Alt+R** Session HUD reset (extend `SessionTracker.Reset`).

### Testing
- Core unit tests: `KillTracker` (HP→0 counts once per rarity; eviction ≠ kill; re-appearing address after eviction handled). `SessionStats` maps/hr + xp-efficiency computed correctly.
- Live smoke: kills tick up as you clear a pack; maps/hr rises per map; xp-efficiency matches player−area level.

---

## Architecture / boundaries

- **Core:** `Game/KillTracker.cs` (new, pure), `Game/EdgeArrow.cs` (new, pure border-intersection helper), `SessionStats`/`SessionTracker` extended (maps/hr, xp-efficiency, kill counts). No new offsets.
- **Overlay:** `RadarApp` — build `EntityArrowTarget` frame (world thread) + feed `KillTracker` + zone-entry map count; publish in `WorldSnapshot`; pass ctx. `RenderContext` — `EntityArrowTarget` + arrow ctx fields; new Session HUD fields. `OverlayRenderer` — `DrawEdgeArrow` (generalized from `DrawAtlasArrow`) + extended `DrawSessionHud`. `Config/RadarSettings` — `EntityArrows` object + `DisplayRule.OffScreenArrow` + Session HUD v2 toggles. `Web/ApiServer` + `Web/DashboardHtml` — settings round-trip + the per-rule arrow checkbox + Session HUD v2 toggles.

## Out of scope (YAGNI)
- Per-rule audio-on-appear (sound stays in the existing audio-alert system).
- Map *completion* detection (only entries).
- Exact server-side kill count (would need offset RE).
- Anything from the other scouted features (streaming, buff icons, Sanctum) — separate releases.

## Version
Ships as **v0.15.0 — Situational Awareness**. README badge stays `0.5.4`. SDD flow.
