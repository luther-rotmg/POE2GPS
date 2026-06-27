# Campaign GPS (Quest-aware Director, Part B) — Design

**Date:** 2026-06-27
**Branch:** `feat/campaign-gps`
**Status:** Approved design — ready for implementation plan

## Problem & goal

The existing **Objective Director** is strictly **intra-zone**: it ranks entities/landmarks in the *current* zone and auto-selects one as the nav target. Nothing tells the player **which zone to go to next** for campaign progression. Goal: a **cross-zone campaign GPS** — point the player to the right zone for their current campaign step, route them to the in-zone exit that leads there, and hand off to the in-zone Director once they arrive. This is the feature that elevates POE2GPS from "radar" to "GPS." Strictly read-only / GGG-compliant.

## The isolation principle (non-negotiable)

The user chose to pursue **both** the zone-order GPS and the quest-memory read this cycle. To avoid coupling the flagship's ship date to an uncertain RE effort (the quest-state read is a deep-pointer grind currently at only a *lead* — `ServerDataStructure +0x3030`, `0xB4000000` sentinel, per-quest mapping unpinned; same risk profile as the shelved Island Rumours feature), the architecture enforces:

> **The zone-order GPS is the shipping deliverable and never depends on the quest read.** The quest-completion read is a strictly **additive precision layer** behind an `IQuestProgress` interface. If the probe session pins the offsets, it slots in and refines routing; if it does not, the feature ships fully on zone-order alone and the memory layer stays disabled behind a kill-switch. No quest-state stub ships enabled.

## Approach (chosen)

Infer campaign position from the player's **current zone code** (the already-validated `AreaInfo` code read) plus a **hand-authored critical-path route table**, with a "furthest-reached" latch so backtracking into an old zone doesn't rewind the GPS. The quest-memory read, if pinned, overrides the inference for optional / out-of-order quests. Rejected: gating v1 on the quest read (blocks the flagship on uncertain RE); a fully runtime-derived zone graph (no connectivity data exists and the `AreaTransitionComponent` destination field is unvalidated).

## Data layer

### `campaign_route.json` (new embedded Core resource, sibling of `world_areas.json`)

A hand-authored ordered critical-path table. Each step:

```
{ "zone": "G1_4", "act": 1, "name": "The Grelwood", "next": "G1_5", "exitHint": "The Red Vale" }
```

- `zone` — area code (matches the in-game `AreaInfo` code).
- `act` — campaign act (1–3 cover Normal **and** Cruel — same zones; higher acts/interlude as the data warrants).
- `name` — friendly name (from `world_areas.json`).
- `next` — the next critical-path zone code (null at an act's final hand-off / campaign end).
- `exitHint` — the **destination label** to match an in-zone exit against, sourced from the curated `CustomLandmarks.json` exit labels + `zone_notes.json` traversal prose + public campaign route knowledge. May be null (→ generic `Exit`/waypoint fallback).

Covers all campaign zones present in `world_areas.json` (G\*/P\* codes). The Act-6 interlude's three parallel branches (P1/P2/P3, each with a town) are represented as branch sequences keyed by zone-code prefix — the player's current zone code selects the branch. Authored once; updated per patch if GGG renames/reorders campaign zones (campaign zones are far more patch-stable than offsets).

### `CampaignRoute.cs` (Core, pure)

Loads + indexes `campaign_route.json`. API:
- `CampaignStep? StepFor(string zoneCode)` — the step whose `zone` == code.
- `CampaignStep? NextStep(CampaignStep step)` — resolves `step.next` to a step.
- `int IndexOf(string zoneCode)` — ordinal position in the critical path (for the latch + "furthest reached").
- `string? CodeForName(string friendlyName)` — a **name→code reverse map** built from `world_areas.json`, so curated exit *names* (e.g. "The Grelwood") resolve back to codes. Case-insensitive; null on miss.
- `record CampaignStep(string Zone, int Act, string Name, string? Next, string? ExitHint)`.

## Engine

### `IQuestProgress` (Core interface — the isolation seam)

```
CampaignStep CurrentStep(string currentZoneCode);
```

Returns the step the player should currently be working toward (their target zone). Two implementations:

- **`ZoneOrderProgress` (v1, pure, TDD'd):** holds the route + a `_furthestIndex` latch. `CurrentStep(zone)`: if the current zone is on the critical path at index ≥ `_furthestIndex`, advance the latch to it and return that step (you're progressing). If the current zone is *behind* the latch (backtracking) or *off* the path (a side zone), return the step at `_furthestIndex` (keep pointing forward — "get back on the critical path"). The latch only moves forward.
- **`MemoryQuestProgress` (v2, gated):** wraps `ZoneOrderProgress` and, when `Poe2Live.TryReadQuestState()` yields a validated completed-quest set, overrides the inferred step (e.g. skip a step whose quest is already done, or surface an out-of-order required quest). Constructed only when the offsets are pinned + `EnableQuestMemory` is on; otherwise never instantiated.

### `CampaignGps.cs` (Core, pure, TDD'd)

`GpsInstruction Decide(string currentZoneCode, IQuestProgress progress, IReadOnlyList<Landmark> inZoneLandmarks, Vector2 player)`:

- `target = progress.CurrentStep(currentZoneCode)`.
- **In the target zone** (`currentZoneCode == target.Zone`) → `InThisZone = true`, no `ExitObjectiveId`; the in-zone Director runs the zone normally. Instruction: `"✓ In <name> — clearing objectives"`.
- **Off the target zone** → resolve the desired next zone toward the target (for a critical-path zone, that's `StepFor(current).Next`; for an off-path zone, the latched target's zone). Choose the in-zone exit by this precedence: (1) a landmark/transition whose curated destination label **equals `StepFor(current).ExitHint`** (name-to-name, when the hint is present); (2) else a landmark whose destination label **resolves via `CodeForName` to the desired next code**; (3) else the nearest generic `Exit`-category transition; (4) else the zone waypoint. Emit `ExitObjectiveId` (the existing nav id `t:<landmarkKey>` / `e:<entityId>`) + instruction `"Act N · → <TargetName> · take the <exitName> exit"`.

`record GpsInstruction(bool InThisZone, string? ExitObjectiveId, string TargetZoneName, int Act, string Text)`.

## Wiring (`RadarApp` — reuses existing seams, zero new routing plumbing)

- **`OnAreaChanged`** advances the `ZoneOrderProgress` latch on each zone entry.
- **`DirectorReconcile`** (gated on new `EnableCampaignGps`): call `CampaignGps.Decide(...)`. If `!InThisZone && ExitObjectiveId != null`, set it as the active nav target via the existing `SetActiveTarget` (priority over in-zone objectives) → the existing A* route/tracker draws the path to the exit (no new rendering). If `InThisZone`, do nothing extra — the normal Director runs.
- Publish the instruction: prepend a synthetic `RankedObjective` (a "Campaign GPS" row) to `_directorQueue` and add a `campaignGps` string to `RadarState` → `/state` → dashboard. (Reuses the `RadarState.Director` channel + a small new field.)

## UX

- **Dashboard:** a "Campaign GPS" instruction line at the **top of the Zone Plan card** ("Act 1 · → Clearfell · *Grelwood* exit" or "✓ in The Grelwood — clearing objectives"), rendered from the `/state` `campaignGps` field. A toggle in the Director/Settings card: **Campaign GPS** (off by default).
- **Overlay:** a compact one-line instruction (reusing the Session-HUD / banner draw path), drawn only when `EnableCampaignGps` and in-game. The route to the chosen exit draws via the existing path pipeline.
- `RadarSettings.EnableCampaignGps` (default **false**, experimental) + `EnableQuestMemory` (default **false**, only meaningful once the read is validated).

## Quest-read attempt (gated, non-blocking)

During the in-game session: run `--quest` / `--serverdata-diff` to pin the `ServerDataStructure +0x3030` quest block — confirm `+0x34xx` is quest-only (control diff), decode several quests to map field→quest + the `0xB4000000` sentinel semantics, and identify the per-quest key (dat-row pointer / internal id). **If** it resolves to a stable completed-quest set: add `Poe2Live.TryReadQuestState() → IReadOnlySet<string>` (guarded, returns empty on any read failure), `MemoryQuestProgress`, and enable `EnableQuestMemory`; bump CLAUDE.md offsets + the validated markers. **If it does not pin cleanly:** ship zone-order only; leave the probes + this section as the resume point (do not grind it indefinitely — the Island Rumours lesson).

## Testing

- **`CampaignRoute` (pure):** lookup, `NextStep`, `IndexOf`, branch resolution (P1/P2/P3), `CodeForName` reverse map (hit + case-insensitive + miss).
- **`ZoneOrderProgress` (pure):** forward advance, backtrack-doesn't-rewind (latch), off-path returns the latched target.
- **`CampaignGps` (pure):** in-target-zone handoff (no exit override), off-target exit selection by destination label, missing-label fallback (generic Exit), and the instruction text.
- **`MemoryQuestProgress` / `TryReadQuestState`:** probe-validated / integration only (live memory, `Poe2Live` pattern), behind the kill-switch — no unit test.
- CI: `compliance-gate.ps1` + `scrub-strings.ps1 -SelfTest` green; full xUnit suite green.

## Compliance

All additions are reads. No input/write/inject APIs; no forbidden symbol names. The quest read (if added) uses the existing `OpenProcess` read mask. The `campaign_route.json` data is offline-authored (no pricing, no identifying data).

## Out of scope (YAGNI)

- Multi-hop pathfinding across several intermediate zones (the player walks zone-by-zone; single "next zone + exit" is the GPS unit). A full zone-connectivity graph is not built.
- Waypoint fast-travel route optimization (prefer-waypoint-hop) — future refinement.
- Auto-completing / auto-advancing quests, or any write to game state — forever out of scope.
- Indefinite quest-state RE — one bounded session attempt; ship zone-order regardless.
