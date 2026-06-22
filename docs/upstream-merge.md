# Syncing read-only improvements from Sikaka

POE2GPS tracks [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar) as `upstream`. The goal is to keep
pulling Sikaka's **read-only** improvements — new/validated **offsets**, radar/atlas/terrain reads, bug
fixes — **without ever re-introducing the gutted non-compliant features** (auto-flask, byte-patching,
poe.ninja/poe2scout pricing).

## The reality (read this before you merge)

Two facts shape the whole strategy:

1. **We're heavily diverged, Sikaka is low-churn.** We're ~70 commits ahead (all our features); Sikaka
   adds only a handful of commits between our syncs. So this is *occasional, surgical cherry-picking*,
   not a routine `git merge`.
2. **Sikaka bundles good + gutted in single commits.** A typical Sikaka commit mixes a read-only win with
   a gutted feature — e.g. `v0.15.0` added a useful **league-name offset/read** *and* the **pricing**
   code that consumes it, in one commit. A blind `git merge upstream/main` would drag the gutted half
   right back in.

**⟹ Do NOT `git merge upstream/main` wholesale.** Review each upstream commit and take only its read-only
parts.

## The ritual (selective)

```bash
git fetch upstream
git log --oneline --no-merges main..upstream/main     # what's new upstream
# For each commit worth pulling, inspect what it actually touches:
git show <sha> --stat
git show <sha> -- <the read-only files you want>       # e.g. Poe2Offsets.cs, Poe2Live.cs
# Apply ONLY the read-only hunks — by hand (Edit the offset/read in) or:
git checkout -p <sha> -- <file>                         # interactively stage just the good hunks
# Reject every hunk that touches a gutted feature (see strip-list).
dotnet build POE2Radar.slnx                             # 0 warnings, 0 errors
dotnet test  POE2Radar.slnx                             # all pass
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1   # must print PASS
```

The compliance gate is the **backstop**: it fails if a merge re-introduces `SendInput`/`WriteProcessMemory`/
`VirtualProtectEx`/etc. **and now also if it restores the pricing layer** (the `Pricing/` dir, `class
PriceBook`, `new PriceBook`, or the `/api/prices` route). Pricing isn't an input/write API, so the gate
was extended to catch it specifically — see `scripts/compliance-gate.ps1`. If the gate is green, no gutted
feature came along for the ride.

## Worked example — the league pull (Sikaka v0.15.0)

`3950bf7 v0.15.0 — auto-detect price league` bundled a read-only win (read the league name from
`ServerData +0x21E0`) with pricing (feed it to `PriceBook`). We pulled **only the read-only half**:

- ✅ **Took:** `Poe2.ServerData.League = 0x21E0` (offset const) + `Poe2Live.LeagueName(areaInstance)` (the
  read). Comments reworded to drop the pricing framing. Commit `b1afa2b`.
- ❌ **Rejected:** the `PriceBook.SetDetectedLeague` wiring, the `RadarApp`/`ApiServer`/`DashboardHtml`
  pricing hooks, `RadarSettings` pricing fields, and the auto-flask-persist change from the sibling commit.
- ⚠️ **Flagged:** Sikaka validated the offset live on HC/SC/Standard; it's **unverified on our build** until
  a live smoke-test. `LeagueName` is `public` and compiles unused, so it sits ready for a future feature.

That's the template: take the offset/read, reject the consumer if the consumer is gutted, let the gate
confirm nothing slipped through.

## Strip-list — what POE2GPS removed (keep it removed)

When a merge conflicts on any of these, resolve by keeping POE2GPS's removal:

- **Auto-flask (input emission).** Deleted: `src/POE2Radar.Overlay/Input/SendInputNative.cs`, the
  `TickAutoFlask` method + its call site, the `F8` hotkey branch, the `AutoFlask`/`FlaskNote` fields
  and all flask config (`LifeKey`/`ManaKey`/thresholds/cooldowns) across `RadarApp.cs`,
  `RadarSettings.cs`, `ApiServer.cs`, `DashboardHtml.cs`. (The read-only HP/Mana/ES vitals readout
  was kept.)
- **poe.ninja / poe2scout pricing.** Deleted: `src/POE2Radar.Overlay/Pricing/PriceBook.cs` and all
  its consumers; the reward/item overlays show **names only** (no value chips). Deleted the
  `/api/prices` route, the dashboard pricing card's value inputs, and the value fields on
  `GroundItemSettings`. **The compliance gate now enforces this** — it fails if the `Pricing/` dir, a
  `PriceBook` class, or the `/api/prices` route reappears, so a careless merge can't quietly restore it.
- **MCP server** (if it ever appears upstream) and any byte-patching `Cheats/` directory — neither
  belongs in POE2GPS.

## High-churn conflict surfaces

These files were edited the most and are where upstream cherry-picks will most often conflict:

- `src/POE2Radar.Overlay/RadarApp.cs` (large, central)
- `src/POE2Radar.Overlay/Web/ApiServer.cs` and `Web/DashboardHtml.cs` (settings whitelist + cards)
- `src/POE2Radar.Overlay/Config/RadarSettings.cs`

## Keep us mergeable (deliberate non-divergence)

Syncability is a *constraint on how we refactor*. To keep pulling Sikaka's read-only fixes cheap, we
**deliberately do NOT restructure the high-churn shared files above**, even when an audit suggests it:

- **We don't split `RadarApp.cs`/`ApiServer.cs`** into controllers/handlers. It would read cleaner, but
  it would turn every future Sikaka change to those files into a merge conflict against code we moved
  out from under it. The audit's "split the 2,187-line `RadarApp`" recommendation is **declined on
  purpose** for this reason (see `docs/audit-2026-06-22.md`).
- **New POE2GPS features live in NEW files** (`Core/Campaign/*`, `Overlay/Web/SeenPoiLog.cs`,
  `EntityAtlasLog.cs`, `Core/Gear/*`, …) with only thin, well-marked *hooks* into the shared files. New
  files never conflict; the hooks are listed below so they're easy to re-apply if a shared file is
  taken wholesale from upstream.
- **Refactors that touch only POE2GPS-owned files are fine** (e.g. deduping our own store boilerplate) —
  they add no upstream conflict surface.

When in doubt: prefer a new file + a one-line hook over editing a shared file's structure.

## What POE2GPS adds on top of Sikaka (preserve on merge)

- `src/POE2Radar.Core/Stealth/RandomName.cs` and the process-randomization wiring in `Program.cs` /
  `Overlay/OverlayWindow.cs` (random hardlink relaunch, random window class/title, neutral
  `AssemblyName`, character-name not exposed on `/state`).
- `scripts/compliance-gate.ps1` + `scripts/compliance-allowlist.txt`, `scripts/scrub-strings.ps1`,
  and the scrub wiring in `publish.ps1`.
- `.github/workflows/ci.yml` (build + test + gate).
- **Objective Director** (`Core/Campaign/CampaignObjective.cs`, `ObjectiveDirector.cs`,
  `Overlay/Web/CampaignObjectives.cs` + `default_campaign_objectives.json`). Shared-file hooks to
  re-apply on merge: `RadarSettings.EnableDirector`; the `RadarApp` ctor store construction; the
  `!_settings.EnableDirector` gate on the `OnAreaChanged` AutoPath auto-add + the `_director.ResetZone()`
  call; the `DirectorReconcile(player)` call in `WorldTick` (after `PruneCompletedTargets`, before
  `MaintainRoutes`); the `RadarState.Director` field + its construction arg + the `/state` `director`
  projection; the `enableDirector` settings round-trip; the dashboard toggle row + `dirCard` + render block.
- **Catalog Builder** (`Core/Campaign/PoiCandidate.cs`, `ObjectiveCatalog.Covers`, `Overlay/Web/SeenPoiLog.cs`).
  Hooks: the `_seenPoiLog` field + ctor construction; `_seenPoiLog.Observe(...)` in `WorldTick` (next to
  `_modCatalog.Observe`); `_seenPoiLog.Flush()` in `Dispose`; the two new `ApiServer` ctor params
  (`CampaignObjectives objectives`, `Func<…SeenPoi> seenPoisProvider`) + the `/api/seen-pois` and
  `/api/objectives` cases + `ApplyObjectives`/`SanitizeObjective`; the `new ApiServer(...)` call args
  `_campaign, () => _seenPoiLog.All`; the Dashboard "Director" tab (button + `data-view="director"` section + `loadDirector`).
- **Entity Atlas** (`Core/Campaign/AtlasEntry.cs` + `AtlasCensus`, `Overlay/Web/EntityAtlasLog.cs`,
  `Overlay/Web/EntityNameStore.cs`, the `EntityNameResolver` user-override layer). Hooks: the
  `_entityAtlas` + `_entityNameStore` fields + ctor construction; `_entityAtlas.Observe(_entities,
  areaCode)` in `WorldTick` **before** the user-hidden cull; `_entityAtlas.Flush()` +
  `_entityNameStore.Flush()` in `Dispose`; the two `ApiServer` ctor params (`Func<…AtlasEntry>
  entityAtlasProvider`, `EntityNameStore entityNames`) + the `/api/entity-atlas`, `/api/entity-atlas/name`,
  `/api/entity-atlas/export`, `/api/entity-atlas/import` cases + `ApplyAtlasName`/`ApplyAtlasImport`; the
  `CampaignObjectives.Covers(in EntityDot)` forwarder; the `new ApiServer(...)` args
  `() => _entityAtlas.All, _entityNameStore`; the Dashboard "Entity Atlas" tab (`data-tab="entatlas"` /
  `data-view="entatlas"` + `loadEntAtlas`). **Name-clash guard:** the Entity Atlas uses the
  `/api/entity-atlas*` + `entatlas`/`_entityAtlas` namespace — distinct from the endgame Atlas-map's
  `/api/atlas`+`/api/atlas-{select,highlight}` + `atlas`/`_atlas`; don't let a merge collapse them.
- **Quick-Target Cycler** (`Core/Navigation/TargetCycler.cs`, `Overlay/Input/XInputNative.cs` +
  `ControllerCycler.cs`). Hooks: `_rankedTargets`/`_activeTargetId`/`_cycleIndicator`/`_nextCycleAt`/
  `_controllerCycler` fields; `BuildRankedTargets` + `Cycle`/`CycleToIndex`/`ApplyActive`/`SetActiveTarget`;
  the `_rankedTargets = …` publish after `_navTargets = BuildNavTargets`; the keyboard + controller blocks
  in `HandleHotkeys`; `RadarSettings.EnableTargetHotkeys`/`EnableControllerCycle` + the `/api/settings`
  round-trip + dashboard toggles; the `RankedTarget`/`CycleIndicator` records + `RenderContext.CycleIndicator`
  + `OverlayRenderer.DrawCycleIndicator`. All read-only (reads keys/XInput; never `SendInput`).
