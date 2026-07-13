# CLAUDE.md audit — 2026-07-13

Read-only audit of `CLAUDE.md` (project root) against the current codebase state
using the `claude-md-management:claude-md-improver` rubric. No changes have been
made to `CLAUDE.md` itself; this file is a triage report and recommendation set.

## Summary

- Files scanned: 3 (`./CLAUDE.md`, `.reference/sikaka/CLAUDE.md`,
  `.reference/nattkh/CLAUDE.md`). Only the project root file is in scope — the
  `.reference/*` files belong to vendored upstream forks and are informational.
- Project root score: **67 / 100 — Grade C+ (borderline B-)**
- Files needing update: **1** (`./CLAUDE.md`), non-blocking
- Prior audit (`docs/audit-2026-06-22.md`) targeted code/security invariants, not
  the guide; this is the first CLAUDE.md-focused audit on record.

## File-by-file assessment

### `./CLAUDE.md` (project root, 142 lines)

**Score: 67 / 100 — Grade C+**

| Criterion             | Weight  | Score  | Notes |
|-----------------------|---------|--------|-------|
| Commands / workflows  | High    | 5/20   | No build, test, run, or Research-probe invocation commands anywhere. |
| Architecture clarity  | High    | 13/20  | Excellent for the three pillars, but silent on ~10 current subfolders. |
| Non-obvious patterns  | Medium  | 14/15  | Offsets table, PoE1→PoE2 fork context, 3-reader threading, atlas math — all top-tier. |
| Conciseness           | Medium  | 13/15  | Dense but readable; one or two long paragraphs could break for skimmability. |
| Currency              | High    | 11/15  | 0.5.4 offsets validated, but the architecture map reflects a pre-v0.14 codebase — several release-tier features are missing. |
| Actionability         | High    | 11/15  | Rules ("re-run Research probes, re-validate, bump README") are actionable; core dev-loop commands are not. |

#### What works well (keep as-is)

- **Non-negotiable rules block** — PoE2-not-PoE1, stay external, opt-in input
  gating, offset discovery lives in Research. Directly load-bearing; would take a
  future contributor 30 minutes of code reading to reconstruct otherwise.
- **Three-pillar layout statement** — the crisp `Core` / `Overlay` / `Research`
  contract is one of the most useful pieces of context in the file.
- **Key facts / offsets table** — validation markers (`✓`), the "was 0x1A8/... pre-
  patch" annotations, and the AreaInstance +0x18 shift note give any next-patch
  hotfix session an instant delta to check.
- **Atlas overlay projection paragraph** — captures a non-obvious mathematical
  result ("NOT a perspective homography") that is easy to get wrong on re-read.
- **Two-thread / three-reader concurrency model** — the split rationale (RPM is
  concurrency-safe; per-instance buffers are NOT) is exactly the kind of thing
  Claude cannot re-derive from code without significant effort.

#### Issues found

##### 1. Zero build / test / run commands (highest-impact gap)

`CLAUDE.md` names projects and files but never states how to build, run, or
exercise them. Newer sessions have to guess the .NET target framework name from
the `Dependencies` line (`net10.0-windows`) and infer the csproj paths from the
pillar list. Concretely missing:

- `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release`
- `dotnet build src/POE2Radar.Research/POE2Radar.Research.csproj -c Release`
- How to invoke Research probes (they are documented as flags — `--hp`,
  `--vitals`, `--devtree`, `--atlas-probe` — but not as executable command lines).
- Whether a solution file exists at the repo root (none was found in this scan;
  worth stating explicitly so agents stop grepping for one).
- Any `dotnet test` target, if a test project exists (none found in `src/`).

Impact: any inline Claude session that skips the beads pipeline will burn tokens
rediscovering these. High weight because every session hits this.

##### 2. Architecture map is missing ~10 real subfolders under `POE2Radar.Core`

Current `src/POE2Radar.Core/` has these top-level folders that `CLAUDE.md` does
not mention at all:

| Folder         | Why it likely matters                                          |
|----------------|----------------------------------------------------------------|
| `Audio/`       | Opt-in alerts — has automation-adjacent surface area           |
| `Campaign/`    | Campaign route / progression data                              |
| `Config/`      | Settings persistence                                           |
| `Gear/`        | Gear scorer (data-sources memory documents its RePoE pipeline) |
| `Health/`      | Vitals-adjacent read layer                                     |
| `Input/`       | Input surface inside Core (worth flagging under the "external" rule) |
| `Navigation/`  | Nav layer (referenced in text as a Pathfinding/route concept but the folder itself is not named) |
| `Presence/`    | Presence detection (Research has a `--presence` probe already) |
| `Remote/`      | LAN / remote surface — carries the LAN write-gate invariant (see `MEMORY.md`) |
| `Session/`     | Session lifecycle / logging                                    |
| `Stealth/`     | Anti-detection surface — a documented Ryan-preference concern  |
| `Support/`     | Cross-cutting helpers                                          |
| `Update/`      | Update-check machinery                                         |

Plus two loose files at the Core root: `AffineFit2D.cs`, `AtlasGeometry.cs` (both
supporting atlas projection).

##### 3. Architecture map is missing several folders under `POE2Radar.Overlay`

Current `src/POE2Radar.Overlay/` has these unmentioned folders:

- `Assets/`, `Audio/`, `Config/`, `Update/`
- `Overlay/Navigation/`, `Overlay/Native/` (nested inside the Overlay project)
- Files: `Bootstrap.cs` (called out in the entry-point paragraph but not as its
  own component), `DiagnosticsLog.cs`, `GearSnapshot.cs`, `UpdateChecker.cs`.

##### 4. `Web/` folder has grown far beyond the described "read-only HTTP API"

`CLAUDE.md` lists `Web/ApiServer.cs` and hints at `/state`, `/entities`,
`/landmarks`, `/api/icons`. The current `src/POE2Radar.Overlay/Web/` folder holds
**~28 files** — including `DashboardHtml.cs`, `DisplayRules.cs`, `JsonStore.cs`,
`ModCatalog.cs`, `PresetStore.cs`, `HttpListenerSseSink.cs`, `SseChannel.cs`,
`LandmarkStore.cs`, `WatchedEntities.cs`, `GearWeightStore.cs`,
`CampaignObjectives.cs`, and more — plus a `presets/` directory and default JSON
data. The audit `docs/audit-2026-06-22.md` already flagged Web as due for a
break-up. At minimum, `CLAUDE.md` should note it as "the API + dashboard + JSON
store cluster" so agents do not open it expecting a single small file.

##### 5. Missing recent release-tier features (v0.14 – v0.19 range)

Per `MEMORY.md` and confirmed by present source files, the codebase now has:

- **Preload Alert** — `PreloadTracker.cs`, `PreloadCatalog.cs`,
  `Poe2LoadedFiles.cs`. FileRoot loaded-files reader; zone-entry DIFF design.
- **Buff Icons** — `BuffCatalog.cs`. Buffs component reader.
- **LAN read-only** access — implied by the `Remote/` folder; carries the
  invariant "write endpoints MUST gate on `RemoteEndPoint.Address` loopback".
- **Atlas ghost-arrow fix / off-screen arrows** — `EdgeArrow.cs`.
- **Item filter engine** — `ItemFilterEngine.cs` (Panorama epic in progress at
  the `main` HEAD per the last five commits).
- **Boss / Runeforge / Rune Monolith / Dynasty / Waystone** catalogs — none named.

None of these are mentioned. Currency drift = a session touching these files has
no way to know the intent from `CLAUDE.md` alone.

##### 6. No pointer to the pipeline / worktree workflow convention

The user's global `~/.claude/CLAUDE.md` prescribes the beads → opencode →
OpenRouter pipeline as **default** for multi-step tasks. Project `CLAUDE.md` is
silent on whether this project participates. A one-line pointer ("this project
uses the standard beads pipeline; see global CLAUDE.md") would prevent Claude
from silently doing the wrong thing on a fresh session where the global file is
not loaded.

##### 7. No pointer to `docs/`

There are useful sibling docs — `docs/release-checklist.md`,
`docs/community-pipeline.md`, `docs/upstream-merge.md`,
`docs/labeling-and-contributing.md`, prior audits — that are not linked from
`CLAUDE.md`. A single "See also `docs/*` for release / audit / contribution
notes" line would raise the surface area for free.

##### 8. Minor: PoE2 patch string appears in three places

The offset validation ("PoE2 0.5.4") repeats without a single canonical
statement. Consider a "Currently validated against PoE2 patch: 0.5.4" line at the
top of the "Key facts" section so future patch-bump edits touch one spot.

## Recommended additions (proposals — NOT applied)

Per the task instruction, `CLAUDE.md` is **not** being modified. The following
diffs are proposed for a future update session:

### Proposal A — add a "Build & run" section right after the pillar list

```diff
+ ## Build & run
+
+ No solution file; build each project directly. Requires **.NET SDK 10.x**
+ and Windows x64.
+
+ ```powershell
+ # Overlay (deliverable exe)
+ dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Release
+
+ # Research (dev-time probes)
+ dotnet build src/POE2Radar.Research/POE2Radar.Research.csproj -c Release
+
+ # Run a Research probe (must have PoE2 open):
+ dotnet run --project src/POE2Radar.Research -c Release -- --devtree
+ dotnet run --project src/POE2Radar.Research -c Release -- --hp
+ dotnet run --project src/POE2Radar.Research -c Release -- --vitals
+ dotnet run --project src/POE2Radar.Research -c Release -- --atlas-probe
+ ```
+
+ No test project ships in-tree; validation is via the Research probes plus the
+ built overlay against a live client.
```

### Proposal B — extend the Core layer section to name the missing folders

Suggested edit near line 56 (end of Core read layer bullets):

```diff
  - `Pathfinding/MapProjection.cs` + `GridConstants.cs` — isometric grid→screen projection and the
    grid↔world scale (250/23 ≈ 10.87).
+ - `Health/`, `Presence/` — read-side vitals + entity presence tracking.
+ - `Gear/` + `Game/StarterWeights.cs` + `Game/TierDeriver.cs` + `Game/ModRanges.cs` —
+   gear scorer (RePoE-derived weights/ranges; see `resources/poe2-data/`).
+ - `Navigation/` — nav-target/route data on the read side (paired with
+   Overlay's `Navigation/` for the tick loop).
+ - `Campaign/` + `Game/CampaignRoute.cs` — campaign-progression route model.
+ - `Session/` — per-session lifecycle + logging.
+ - `Config/` — settings persistence (mirror in Overlay's `Config/`).
+ - `Audio/` — opt-in alert sounds (still a *read*-side notification, no input).
+ - `Update/` + Overlay `UpdateChecker.cs` — opt-in GitHub update check; gated
+   by `RadarSettings.CheckForUpdates` (see 2026-06-22 audit).
+ - `Remote/` — **LAN read-only surface.** Write endpoints MUST gate on
+   `RemoteEndPoint.Address` loopback (Host header is spoofable).
+ - `Stealth/` — anti-detection footprint controls; treat with care.
+ - `Support/`, `AffineFit2D.cs`, `AtlasGeometry.cs` — helpers used by atlas
+   projection + generic math.
+ - Recent-release feature readers (v0.14+): `PreloadTracker.cs` +
+   `PreloadCatalog.cs` + `Poe2LoadedFiles.cs` (Preload Alert), `BuffCatalog.cs`
+   (Buff Icons), `EdgeArrow.cs` (off-screen atlas arrows),
+   `ItemFilterEngine.cs` (Panorama epic — item filter matches),
+   `BossEncounterCatalog.cs`, `WaystoneModRisk.cs`,
+   `RuneMonolithCatalog.cs`, `Poe2Runeforge.cs`, `DynastyMaps.cs`,
+   `AtlasMapData.cs`, `Poe2Atlas.cs`, `MonsterAffixCatalog.cs`,
+   `MechanicPatterns.cs`, `JunkFilter.cs`, `ZoneGuide.cs`,
+   `EntityNameResolver.cs`, `CustomLandmarkData.cs`.
```

### Proposal C — expand the Overlay Web/ description

Suggested edit near line 86:

```diff
- - `Web/ApiServer.cs` — read-only HTTP API on `localhost:7777` (`/state`, `/entities`, `/landmarks`,
-   `/api/icons` — the icon library for the dashboard's SVG-preview shape pickers).
+ - `Web/` — the API + dashboard + JSON-store cluster (~28 files). Entry point
+   `ApiServer.cs` serves read-only HTTP on `localhost:7777` (`/state`,
+   `/entities`, `/landmarks`, `/api/icons`) plus an SSE stream
+   (`SseChannel.cs` / `HttpListenerSseSink.cs`). The dashboard HTML is inlined
+   in `DashboardHtml.cs` (flagged for extraction in the 2026-06-22 audit).
+   Persisted JSON stores: `LandmarkStore`, `WatchedEntities`, `PresetStore`,
+   `GearWeightStore`, `ModCatalog`, `CampaignObjectives`, `HiddenEntities`,
+   `SeenPoiLog`, `EntityAtlasLog`, `EntityDeltaState`, `EntityNameStore`,
+   `LabelVocabulary`, `LandmarkPatterns`, `DisplayRules` — all use
+   `JsonStore.cs`. **Any new write endpoint MUST gate on the loopback rule
+   (see Remote/ notes).**
```

### Proposal D — add "Currently validated" line at top of Key facts

```diff
  ## Key facts (validated live; re-verify per patch)

+ **Currently validated against PoE2 patch:** 0.5.4 (see README badge).
+
  - Chain: AOB "Game States" → GameState → InGameState (active state) → ...
```

### Proposal E — one-line pointer to the pipeline convention + docs/

Suggested addition near the end (before or after `## Dependencies`):

```diff
+ ## Workflow
+
+ This project participates in the standing beads → opencode → OpenRouter
+ pipeline (see `~/.claude/CLAUDE.md`). Frontier sessions decompose; workers
+ execute. Escape-hatch: "just do it directly" is fine when the spec-writing
+ cost exceeds the code cost.
+
+ Related docs live under `docs/`: `release-checklist.md`,
+ `community-pipeline.md`, `upstream-merge.md`,
+ `labeling-and-contributing.md`, plus prior audits (`audit-2026-*.md`,
+ `claude-md-audit-2026-07.md`).
```

## Not proposed (deliberately excluded)

- Duplicating information from `README.md` (supports-patch badge, overall
  pitch). CLAUDE.md is a contributor guide, not a mirror.
- Restating rules already implicit in the code (e.g. "csproj files use x64"
  is visible in every csproj).
- Adding a "Style Guide" or generic .NET conventions section. There is no
  project-specific style rule worth pinning that is not already visible.
- Documenting the beads pipeline mechanics — those belong in the global
  `~/.claude/CLAUDE.md` (and already are).

## Suggested next step

If any of Proposals A–E look right, open a bead titled
`docs: refresh CLAUDE.md with build commands + missing subfolders`, spec the
proposed diffs above, and let the pipeline apply them. Estimated size:
**~60 net LOC of markdown**, single file, no code impact — an ideal
`size:small` worker bead.
