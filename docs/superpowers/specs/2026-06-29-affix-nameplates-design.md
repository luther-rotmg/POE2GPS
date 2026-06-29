# Affix Nameplates — Design

**Date:** 2026-06-29
**Branch:** `feat/v0.12.0-affix-nameplates`
**Status:** Approved direction — spec for implementation

## Goal

An **opt-in** overlay feature that draws an elite monster's chosen **modifiers** (the dangerous
map-mob mods/auras — *Extra Fast, Volatile, Mana Siphoner, Allies cannot Die*, etc.) as floating
text **directly above the monster's head, in screen space** — no mouse hover required. Each elite
shows *its own* affixes, filtered to what you care about. Reuses the already-validated camera
world→screen projection that HP bars use.

## Compliance invariant (non-negotiable)

Read-only / output only. We only **read** monster modifiers (already read today by `ReadMods`) and the
camera `WorldToScreen` matrix (already read every render frame), and **draw text**. No new offsets, no
input/process-write APIs, no Core→Vortice dependency. Default **OFF**. All config writes loopback-gated.
Compliance gate + scrub stay green. README badge stays `0.5.4`.

> Doc note: `CLAUDE.md` lists "camera world→screen matrix" under **Still TBD** — that is **stale**. The
> matrix (`InGameState +0x368` → Camera `+0x1A0`, 64-byte row-major Matrix4x4) is validated and in
> production (HP bars, item labels, world paths). This feature reuses it; update the CLAUDE.md note as
> part of the work.

## Feasibility (confirmed by codebase exploration)

- **World→screen is solved & in production.** `Poe2Live.CameraMatrix(inGameState)` returns the live
  16-float matrix; `OverlayRenderer.DrawNameplates`/`DrawItemLabels` already project a world position to
  `(sx, sy)` and draw multi-line text above a moving entity. A nameplate copies this block.
- **Monster affixes already read.** `ReadMods` runs for `EntityCategory.Monster`, caches per-mob,
  budget-gated (`ModReadBudgetPerPass = 16`), and stores the internal mod ids on `EntityDot.ModList`.
  `ModCatalog` accumulates the observed vocabulary into `config/known_mods.json`.
- **Per-mob world position** comes from `Poe2Live.TryLiveBarAt(render, life, out world, …)` (the HP-bar
  render path); pass `life = 0` to get position only.

## Architecture

Heavy work (read → filter → translate) stays on the **world thread**; the **render thread** only
positions and draws — matching the project's two-thread idiom.

### 1. Core — `MonsterAffixCatalog` (pure, unit-tested) — the heart
`src/POE2Radar.Core/Game/MonsterAffixCatalog.cs` + embedded `src/POE2Radar.Core/Game/poe2_monster_mod_names.json`.

- **Curated table** `poe2_monster_mod_names.json`: `modId → { "name": string, "tier": "Deadly|Notable|Minor" }`.
  Ships with a hand-authored **starter set** of the well-known dangerous monster mods; everything else
  falls through to prettify. (The exact internal ids for curated entries are validated against
  `known_mods.json` / a `--mods` run during implementation — see Testing.)
- `enum AffixTier { Minor = 0, Notable = 1, Deadly = 2 }` (ordered so threshold comparison is `>=`).
- `readonly record struct AffixInfo(string Name, AffixTier Tier)`.
- `AffixInfo Resolve(string modId)` — curated lookup; else **auto-prettify**: strip a leading `Monster`
  prefix, split camelCase / digit boundaries, title-case → readable name; tier defaults to `Minor`.
  Pure, deterministic.
- `readonly record struct AffixLine(string Name, AffixTier Tier)`.
- `IReadOnlyList<AffixLine> Select(IEnumerable<string> mobModIds, AffixFilter f)` — resolves each id,
  applies the filter, returns ordered (Deadly→Minor, then alphabetical), de-duplicated, capped at
  `f.MaxLines` lines. **Pure → unit-tested.**
- `readonly record struct AffixFilter(AffixTier Threshold, IReadOnlySet<string> AlwaysShow,
  IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines)`.
- **Selection logic (exact):** for each distinct mod id on the mob:
  - if `f.Hide` contains the id → never show (overrides everything else);
  - else if `f.DisplayAll` → show;
  - else if `f.AlwaysShow` contains the id → show;
  - else show iff `Resolve(id).Tier >= f.Threshold`.
  Then order by tier desc, then name asc; de-dupe by name; take `MaxLines`.

### 2. Overlay settings — `RadarSettings.AffixNameplates` (default OFF)
`src/POE2Radar.Overlay/Config/RadarSettings.cs` — new `AffixNameplateSettings` sub-class (pattern of
`HpBarSettings`): `Enabled = false`, `Tier = "Deadly"` (threshold), `AlwaysShow = []`, `Hide = []`,
`DisplayAll = false`, `ShowOnRare = true`, `ShowOnUnique = true`, `ShowOnMagic = false`, `MaxLines = 4`,
`OffsetY = -46` (px above the mob, clear of the HP bar's `-30`), and per-tier text colors
(`DeadlyColor`/`NotableColor`/`MinorColor`, defaults: red / orange / light-grey). Round-trips via the
existing JSON serializer; missing keys fall back to defaults.

### 3. World thread — `BuildAffixSpecs()` (parallel to `BuildHpSpecs`)
`src/POE2Radar.Overlay/RadarApp.cs`. When `AffixNameplates.Enabled`: for each monster entity passing the
rarity gate (`ShowOnRare/Unique/Magic` vs `e.Rarity`) that has a non-empty `ModList`, call
`MonsterAffixCatalog.Select(e.ModList, filter)`; if the result is non-empty, capture an
`AffixNameplateSpec(nint Render, AffixLine[] Lines)` (reuse `TryBarComponents` to get the `Render`
address, as `BuildHpSpecs` does). Publish `IReadOnlyList<AffixNameplateSpec>` in the `WorldSnapshot`
(volatile swap, same as `HpSpecs`). The `AffixFilter` is built once per pass from settings.

### 4. Render thread — `OverlayRenderer.DrawAffixNameplates(ctx)`
`src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`. For each spec: `TryLiveBarAt(spec.Render, 0, out world, …)`
→ project with the camera matrix (the exact `DrawNameplates` block) → if on-screen, draw the lines
stacked upward from `(sx, sy + OffsetY)`, each line colored by its tier, with a subtle semi-transparent
backing rect for legibility (the `DrawItemLabels` pattern). Reuse the existing `_tf` text format. Drawn
only when `Enabled` and PoE2 is focused. **Independent of HP bars** (works with HP bars off).

### 5. Dashboard card + API
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs`: a new **Affix nameplates** card (collapsible, off by
  default): master toggle · tier dropdown (Deadly / Deadly+Notable / All) · **Display all affixes**
  switch · rarity checkboxes (Rare/Unique/Magic) · max-lines · a **per-affix override editor** that
  lists the curated masterlist (name + tier badge) merged with the live `known_mods.json` vocabulary,
  letting you mark each **Always show** / **Hide**.
- `src/POE2Radar.Overlay/Web/ApiServer.cs`: `GET/POST /api/affix-nameplates` (loopback-gated) for the
  settings; the override editor reads the masterlist via a new `GET /api/affix-catalog` (curated entries
  + observed ids, each with resolved name + tier).

### Data flow
`ReadMods` (world, existing) → `EntityDot.ModList` → `BuildAffixSpecs` filter+translate (world) →
`WorldSnapshot.AffixSpecs` → render thread `TryLiveBarAt` + camera-project + draw.

## Testing

- **Core unit tests** (`tests/POE2Radar.Tests`): `MonsterAffixCatalog` — `Resolve` (curated hit; prettify
  fallback for `MonsterExtraFast` → "Extra Fast"); `Select` (tier threshold include/exclude; `AlwaysShow`
  promotes a below-threshold mod; `Hide` suppresses even under DisplayAll; `DisplayAll` shows all;
  `MaxLines` cap; ordering Deadly→Minor; de-dup). Pure → fits the Core-only test project.
- **Curated-id validation:** during implementation, cross-check the curated `modId`s against real reads —
  run `dotnet run --project src\POE2Radar.Research -c Release -- --mods` (or inspect `config/known_mods.json`)
  and correct any curated ids that don't match what `ReadMods` returns. Mods not curated still display
  (auto-prettified), so an incomplete starter table is safe, not broken.
- **Overlay** (build + review + live smoke): the card toggles; nameplates draw above a rare's head with
  the right affixes; tier threshold + overrides + display-all behave; nothing draws when off or unfocused;
  no flicker as the mob moves (rides the proven HP-bar position path).

## Out of scope (YAGNI)

- Numeric mod **rolls** (monster mods are binary on/off; only the id/name matters).
- Nameplates for non-monster entities (items already have labels; players/NPCs out of scope).
- In-overlay editing of the filter (dashboard only).
- A generated complete masterlist from RePoE game data (curated starter + auto-prettify + observed
  vocabulary covers v1; a `--gen` probe can come later if desired).

## Version

Ships as **v0.12.0** (feature-sized, x.Y.0 convention). README badge stays `0.5.4`. SDD flow.
