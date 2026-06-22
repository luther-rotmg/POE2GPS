# Merging upstream updates from Sikaka

POE2GPS tracks [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar) as `upstream`. Pull radar /
offset / atlas improvements without re-introducing the removed non-compliant features.

## The ritual

```bash
git fetch upstream
git merge upstream/main
# resolve conflicts (see strip-list below) — KEEP the deletions
dotnet build POE2Radar.slnx
dotnet test POE2Radar.slnx
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1   # must print PASS
```

The compliance gate is the backstop: if a merge silently re-introduces `SendInput`,
`WriteProcessMemory`, `VirtualProtectEx`, etc., the gate fails and tells you exactly where.

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
  `GroundItemSettings`.
- **MCP server** (if it ever appears upstream) and any byte-patching `Cheats/` directory — neither
  belongs in POE2GPS.

## High-churn conflict surfaces

These files were edited the most and are where upstream merges will most often conflict:

- `src/POE2Radar.Overlay/RadarApp.cs` (large, central)
- `src/POE2Radar.Overlay/Web/ApiServer.cs` and `Web/DashboardHtml.cs` (settings whitelist + cards)
- `src/POE2Radar.Overlay/Config/RadarSettings.cs`

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
