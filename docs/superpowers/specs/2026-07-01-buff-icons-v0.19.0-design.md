# v0.19.0 — Buff Icons — Design / Spec

**Date:** 2026-07-01
**Branch:** `feat/v0.19.0-buff-icons`
**Status:** Design approved (Ryan: "go"; display = **text tags**). Buffs component layout validated live via `--buffs` probe. Zero *new* discovery risk; one new (validated) read.

## Goal

Show, above **elite monsters**, the **combat-relevant buffs/debuffs** they currently have — so the player can react to a dangerous aura, enrage, shield, speed, etc. — as short tier-colored text tags. Opt-in, stealth-gated, read-only.

## Compliance / stealth invariant (non-negotiable)

Strictly **read-only**. The only new memory access is the **validated Buffs component** ([[poe2gps-buffs-reader-validated]]) — no new offset *discovery*, no writes/input/pricing. Per Perf v3 (Stealth Reads): the feature **defaults OFF**, and when off it adds **zero reads** (gated exactly like `EnableModReads`/affix nameplates). `compliance-gate.ps1` + scrub stay green. README badge stays `0.5.4`. Version → `0.19.0`.

## Validated read layout (from the probe)

- `ResolveComponent(entity, "Buffs")` — by name (no locate offset).
- `Buffs + 0x160` = `StdVector<StatusEffect*>` (First/Last/End, stride `0x08`); `count=(Last-First)/8`.
- `StatusEffect`: `+0x08` = Definition ptr → `+0x00` = ptr to **UTF-16 buff id**; `+0x18` timer float, `+0x1C` maxTimer (`∞`/`0x7F800000` = permanent), `+0x40` charges.

## Architecture — mirrors the shipped affix-nameplate feature end-to-end

### Core (`POE2Radar.Core`) — the read + the catalog (unit-tested)
1. **`Game/Poe2Offsets.cs`** — add `Poe2.BuffsComponent.BuffVector = 0x160` and `Poe2.StatusEffect { Definition=0x08, Timer=0x18, MaxTimer=0x1C, Charges=0x40 }`, `Poe2.BuffDefinition.IdPtr = 0x00`. Mark `✓` (validated live 2026-07-01).
2. **`Game/Poe2Live.cs`** — new `IReadOnlyList<BuffState> Buffs(nint entity)`:
   - Resolve "Buffs" comp (reuse the component cache); read the `+0x160` StdVector; for each `StatusEffect*`, read the id (`Utf16(*(*(S+0x08)+0x00))`), timer, maxTimer. Return `BuffState(string Id, float Timer, bool Permanent)`.
   - **Cache the id per StatusEffect address** (ids are static per instance) — a `Dictionary<nint,string> _buffId` cleared on zone change + `EvictEntity` (mirror `_mods`/`_openedChests`). Timer is re-read live (it ticks).
   - Gated by `public bool EnableBuffReads { get; set; } = true;` (RadarApp sets it false unless the feature is on — mirror `EnableModReads`).
   - `BuffState` record is a public Core type.
3. **`Game/BuffCatalog.cs`** (new, mirror `MonsterAffixCatalog`) — `Shared` singleton loads embedded **`poe2_notable_buffs.json`** (`id → { display, tier, color }`). `Select(IReadOnlyList<BuffState> buffs, BuffFilter filter) → BuffLine[]`:
   - Catalogued id → its display/tier/color. Uncatalogued → **substring heuristic tier** (`*_aura`/`*aura*`→Notable danger, `enrage*`/`*berserk*`→Deadly, `*shield*`/`*fortif*`→Notable, `*_speed*`/`*haste*`→Notable, else Minor) + **auto-prettified** display (`igniting_presence_aura` → "Igniting Presence Aura").
   - Filter: min tier (Deadly / NotableAndAbove / All), an explicit **hide-set** of known-junk ids (`enemies_in_presence_events_tracker`, `crossbow_should_aim_at_ground`, `*_events_tracker`, `*_reservation`, …), a `DisplayAll` diagnostic bypass, and a `MaxLines` cap. Appends remaining-time to the text for temporary buffs (`"Fire Aura 3s"`; permanent → no suffix).
   - `BuffLine(string Text, BuffTier Tier, string Color)` — Core type (pure; drives the render). `BuffTier { Minor, Notable, Deadly }`.
4. **`resources/` / embedded** — a small starter **`poe2_notable_buffs.json`** (a dozen known combat buffs + the junk hide-list), embedded like `poe2_monster_mod_names.json`. Grown from diagnostic data over time.

### Overlay (`POE2Radar.Overlay`) — spec/frame/render + config (live-smoke)
5. **`RadarApp.cs`** — clone the affix pattern:
   - `BuffNameplateSpec(nint Render, BuffLine[] Lines)` record; `_buffFrame` render-scratch list.
   - `BuildBuffSpecs()` (world rate, beside `BuildAffixSpecs`): if `!cfg.Enabled` set `_live.EnableBuffReads=false` and return empty; else for each **Monster** entity passing the per-rarity gate (default: Rare/Unique/Boss only), call `_live.Buffs(e.Address)` → `BuffCatalog.Shared.Select(...)` → if non-empty and `TryBarComponents` gives a Render addr, add a `BuffNameplateSpec`. Set `_live.EnableBuffReads = cfg.Enabled` before the entity walk.
   - Publish `BuffSpecs` in `WorldSnapshot`; render `Tick()` converts each via `_liveRender.TryLiveBarAt(spec.Render, 0, out world,…)` → `_buffFrame` (world-pos only, HP not needed) — the exact affix-nameplate render path; clear `_buffFrame` in the not-fresh/else block.
   - `RenderContext.BuffTargets` (list of `BuffNameplateTarget(Vector3 World, BuffLine[] Lines)`) + `RenderContext.BuffNameplates` (the settings for styling).
6. **`Overlay/OverlayRenderer.cs`** — `DrawBuffNameplates(rt, ctx)` (clone of `DrawAffixNameplates`): project each target via the camera matrix; draw the tier-colored lines **offset below the mob** (affixes sit above; buffs below) so both coexist. Null-check `ctx.CameraMatrix` (already gated by SR-5 to be present when this feature is on — add BuffNameplates.Enabled to that gate).
7. **`Config/RadarSettings.cs`** — `BuffNameplateSettings` (mirror `AffixNameplateSettings`): `Enabled=false`, `ShowOnMagic=false/ShowOnRare=true/ShowOnUnique=true`, `Tier` ("Deadly"/"NotableAndAbove"/"All", default "NotableAndAbove"), `DisplayAll=false` (diagnostic show-all + prettify), `Hide`/`AlwaysShow` id lists, `MaxLines=4`, text size/color reuse the nameplate style knobs.
8. **`Web/ApiServer.cs`** — `/api/buff-nameplates` GET/POST (round-trip + `TryParseBuffNameplates` sanitize, mirror `TryParseAffixNameplates`); include `buffNameplates` in ReadSettings; a **diagnostic feed** (`/api/buffs`) listing observed buff ids + tier (like `/api/preload` diagnostic) to grow the catalog.
9. **`Web/DashboardHtml.cs`** — a "Buff Icons" settings card (enable, min-rarity, tier, show-all diagnostic, max) + a diagnostic panel of observed buff ids (with their current tier) — mirrors the affix + preload cards.

## Stealth accounting (Perf v3 alignment)
- Default OFF → `EnableBuffReads=false` → `Poe2Live.Buffs` is never called → **zero added reads**.
- On → reads only for **elite monsters passing the rarity gate**, and the buff **id is cached per StatusEffect** (only the small timer float re-reads each world tick). Reads scale with on-screen elites, not all monsters.

## Testing
- **Core unit tests** (test project is Core-only): `BuffCatalog.Select` — catalogued mapping, substring-heuristic tiering, junk hide-list suppression, `DisplayAll` prettify, `MaxLines` cap, timer-suffix formatting, min-tier filter. `BuffState`/`BuffLine` value semantics. (Mirror the `MonsterAffixCatalog` tests.)
- **Live smoke:** enable Buff Icons, stand near a rare/unique with an aura → tier-colored tag appears below it with the right label + timer; toggle off → gone and `rpmPerSec` unchanged from baseline.

## Out of scope (YAGNI)
- SVG buff icons (text tags first; icons are a later polish pass).
- Player/party buff HUD (enemy-facing only for v0.19.0).
- Debuff-on-self tracking, buff-duration bars, sound cues (later).
- Any new offset discovery — the Buffs layout is validated.

## Version
Ships as **v0.19.0 — Buff Icons**. SDD flow (per-task adversarial review + whole-branch final review). README badge stays `0.5.4`. Discord announcement drafted at release. The starter `poe2_notable_buffs.json` is intentionally small; the diagnostic feed grows it (community, like the affix masterlist + preload catalog).
