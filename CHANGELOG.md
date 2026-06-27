# Changelog

All notable changes to POE2GPS. This project is a strictly read-only, GGG-compliant PoE2 navigation overlay.
Versions are GitHub release tags (`vX.Y.Z`); the in-app update checker compares against the latest.

## [0.7.1] — 2026-06-27
### Added
- **Force re-scan** button (Status card) + `POST /api/rescan` — re-detect the game after a patch without restarting.
- **Health pill** in the dashboard masthead — game read-state (in game / connecting / out-of-date) on every tab.
- Controller bindings (R3/L3) in the console hotkey list; the version is now prominent.
### Changed
- WorldLoop clears the radar + rate-limits the log if a tick throws (no stale data / log flood).
- Removed dead/INVALID unreferenced offset stubs from the offset table (internal hygiene).

## [0.7.0] — 2026-06-27
### Added
- **Campaign GPS** (experimental, off by default) — cross-zone campaign navigation: routes you toward the next critical-path zone's exit, shown on the dashboard Zone Plan + overlay.

## [0.6.0] — 2026-06-26
### Added
- **Patch-resilience & health/status** — the overlay self-detects when a PoE2 patch breaks the offsets, starts non-fatally and self-connects (works when launched at login), re-attaches after a game restart, and shows a clear update-aware banner + dashboard Status panel instead of failing silently.

## [0.5.2] — 2026-06-26
### Changed
- Target cycling follows the radar-menu order by default; "Intelligent target cycling" (priority/distance) is an opt-in toggle. Hold-to-fast-cycle on controller + keyboard.

## [0.5.1] — 2026-06-25
### Fixed
- PoE2 **0.5.4** offset hotfix (AreaInstance +0x18 insertion: LocalPlayer/ServerData/entities/terrain re-validated).

## [0.5.0] — 2026-06-24
### Added
- Objective Director v2: tier-aware ranking + Zone Plan + a suggest-only classifier. Plus stealth `.exe` cleanup and atlas node-centering fixes.

## [0.4.0] — 2026-06-23
### Added
- Session HUD (opt-in, off by default): pace / zone context / deaths, on the overlay + dashboard.

## [0.3.2] — 2026-06-24
### Added
- In-app Discord link in the dashboard tab bar and console banner at startup.

## [0.3.1] — 2026-06-24
### Changed
- One-click Contribute is now live: the collector is deployed; the Contribute button uploads directly with no user setup required.

## [0.3.0] — 2026-06-23
### Added
- Community Pipeline: Cloudflare Worker collector with auto-filter (profanity/junk/identifying/oversized) and GitHub issue filing for maintainer review. `merge_community.py` for merging approved packs.

## [0.2.2] — 2026-06-23
### Added
- Richer classify labels: curated grouped vocabulary (~40 labels, autocomplete) served from `/api/labels`; custom typed labels still accepted.

## [0.2.1] — 2026-06-23
### Added
- **Dynasty-support map highlighting**: Sealed Vault / Sacred Reservoir / Derelict Mansion ring purple + label + auto-route on the Atlas (first community request).

## [0.2.0] — 2026-06-23
### Added
- Console glow-up: POE2GPS ASCII banner, color-coded startup, and full hotkey reference in the cmd window.
- Community Contribute (pipeline groundwork): one-click entity name + label upload.

## [0.1.9] — 2026-06-23
### Added
- **Gear Scorer v2**: meta-derived starter weights out of the box; stat-ID chips on every affix; rarity colors + grid heatmap.

## [0.1.8] — 2026-06-22
### Added
- **Quick-Target Cycler**: Ctrl+Alt+]/[ cycles radar targets next/prev; Ctrl+Alt+1–9 jumps to a slot; controller L3/R3 support.

## [0.1.7] — 2026-06-22
### Added
- **God-Roll Detector** (experimental): per-affix roll quality and tier badge on identified gear; affixes at ≥ 90 % of max highlighted gold.

## [0.1.6] — 2026-06-22
### Changed
- Footprint & cleanup: reduced binary footprint, stealth process name, miscellaneous robustness fixes.

## [0.1.5] — 2026-06-22
### Added
- **Map the game together**: community entity pack import/export (`atlas-pack.json`) for sharing discovered entity names and labels.

## [0.1.4] — 2026-06-22
### Changed
- Stealth & robustness: process randomization on launch; no stray `.exe` left on exit; hardened attach loop.

## [0.1.3] — 2026-06-22
### Added
- **Entity Atlas**: dashboard tab for naming and classifying discovered entities/POIs; names show live on the radar.

## [0.1.2] — 2026-06-22
### Added
- **Catalog Builder**: dashboard Director tab for adding uncatalogued POIs/landmarks to the Objective Director catalog.

## [0.1.1] — 2026-06-22
### Changed
- Minor fixes and stability improvements over the initial release.

## [0.1.0] — 2026-06-21
### Added
- Initial release: read-only PoE2 navigation overlay with radar, atlas projection, Objective Director, and HTTP API.
