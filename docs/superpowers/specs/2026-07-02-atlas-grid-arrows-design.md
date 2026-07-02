# Atlas Grid-Based Off-Screen Arrows — Design

**Date:** 2026-07-02
**Status:** approved (design), pending spec review
**Release:** v0.19.6 (finishes the atlas-arrow saga: v0.19.5 disabled off-screen arrows; this brings them back, accurate)

## Goal

Bring back the Atlas off-screen edge-arrows (point me toward my tracked Citadels/maps) **working correctly**, by deriving each arrow's direction from the target node's **stable grid coordinate** instead of the game's unreliable off-screen position.

## Background (why the old ones were "ghosts")

PoE2 stops updating a UI element's `RelativePos` once the game culls it off-screen, so an off-screen node's read position is stale/garbage (the diagnostic showed swings to ±1.8 M). The old edge-arrows pointed at that garbage → "arrows to nothing." v0.19.5 disabled them. But each node also carries a **grid coordinate** (`AtlasNode.GridPos +0x320` → `AtlasNodeLive.GridX/GridY`) that is **stable whether or not the node is on-screen** (it's an integer on the node's data object, not the layout engine). On-screen nodes give us `(grid → screen)` sample pairs; from those we can fit a transform and use it to place any off-screen node's target reliably.

## Architecture (2–3 sentences)

A pure, unit-tested Core solver (`AffineFit2D`) fits a 2D affine `screen = M·grid + b` by least squares from the on-screen nodes' `(grid, screen)` pairs. `AtlasMark` gains `GridX/GridY` (already read; just forwarded). In `OverlayRenderer.DrawAtlas`, on-screen nodes are collected as anchors, the affine is fit once per frame, and each off-screen **arrowed** node's target screen position is computed from its grid via the fit, then the existing `DrawEdgeArrow` points there.

## Tech Stack

.NET 10, C#, x64, Windows. Vortice.Direct2D1 (render). xUnit (Core solver). No new memory reads (grid coords are already read).

## Global Constraints (binding)

- **Strictly READ-ONLY of the game.** No new reads at all — this is pure render-side math over data already read. No input, no process writes, no pricing.
- Version → **0.19.6** in `POE2Radar.Overlay.csproj`.
- CI gates unchanged: `compliance-gate.ps1` + `scrub-strings.ps1 -SelfTest` must pass; full test suite green.
- **Verify before public default-on:** given the arrows can't be reproduced/validated on the dev machine and this closes a 5-patch saga, the built binary is handed to the reporter as a **pre-release test build** to confirm the arrows point true, *then* the public tag is cut. No blind public default-on.

## Components

### 1. Core — `AffineFit2D` (pure, unit-tested)

`src/POE2Radar.Core/AffineFit2D.cs`. A 2D affine least-squares fit + apply. No dependencies.

- `readonly record struct Affine(double A, double B, double C, double D, double E, double F)` — `screenX = A·gx + B·gy + C`, `screenY = D·gx + E·gy + F`.
- `static bool TryFit(IReadOnlyList<(float Gx, float Gy, float Sx, float Sy)> anchors, out Affine fit)`:
  - Requires ≥ 3 anchors. Builds the shared 3×3 normal matrix `N = Σ [gx,gy,1]ᵀ[gx,gy,1]` and two RHS vectors `rx = Σ [gx,gy,1]ᵀ·sx`, `ry = Σ [gx,gy,1]ᵀ·sy`. Solves `N·[A,B,C]=rx`, `N·[D,E,F]=ry` by inverting the 3×3 (both axes share `N`).
  - Returns **false** if fewer than 3 anchors OR `|det(N)| < ε` (degenerate / collinear grids) — caller then draws no off-screen arrows this frame (same as the current disabled state). Never throws.
- `static (float Sx, float Sy) Apply(in Affine fit, float gx, float gy)` — project a grid coord to screen.

Rationale for affine (not perspective): `AtlasProjection` is already affine (persp coeffs 0), and the atlas grid is a near-regular lattice, so `grid → canvas → screen` is affine. Minor lattice jitter only perturbs the estimate slightly — for an **arrow (direction only, clamped to the screen edge)** that is more than accurate enough.

### 2. Overlay plumbing — carry grid into the mark

`src/POE2Radar.Overlay/Overlay/RenderContext.cs` — add `int GridX = 0, int GridY = 0` to `AtlasMark` (append as optional params so no other call site breaks).
`src/POE2Radar.Overlay/RadarApp.cs` (~3261, `BuildAtlasMarks`) — pass `n.GridX, n.GridY` into the `new AtlasMark(...)`. The SR-10 cull's `m with { X=…, Y=… }` (RadarApp.cs ~1388) preserves the new fields automatically (record `with`), so they reach the renderer unchanged.

### 3. Render — grid-based arrow direction in `DrawAtlas`

`src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (`DrawAtlas`, ~306-382):

- **Pass 1 (existing loop):** as it iterates marks and computes each `(sx, sy)` + `onScreen`, when a node is on-screen collect an anchor `(GridX, GridY, sx, sy)` into a reused scratch list. Draw on-screen rings exactly as today (unchanged).
- **Off-screen arrowed nodes:** instead of drawing an arrow toward the (garbage) projected relPos, **defer** them — collect the off-screen `n.Arrow` marks into a small scratch list during pass 1 (they need the fit, which isn't ready until all anchors are seen).
- **After pass 1:** `if (AffineFit2D.TryFit(anchors, out var fit) && anchors.Count >= MIN_ANCHORS)` then for each deferred off-screen arrowed node compute `(ax, ay) = AffineFit2D.Apply(fit, n.GridX, n.GridY)` and `DrawEdgeArrow(rt, ax, ay, ccx, ccy, W, H, col, n.Label)`. If the fit fails (too few anchors / degenerate), draw no off-screen arrows (graceful — same as v0.19.5).
- `MIN_ANCHORS` = a small constant (e.g. 4) for a stable fit.
- Reuse scratch lists (fields on `OverlayRenderer`) to avoid per-frame allocation, matching the renderer's existing allocation discipline.

### Edge cases

- **< MIN_ANCHORS on-screen** (panned to sparse/empty area): no fit → no off-screen arrows that frame. Acceptable (rare; nothing to anchor against anyway).
- **Degenerate/collinear anchors:** `det(N) ≈ 0` → `TryFit` false → no arrows.
- **A node with grid (0,0):** used as an anchor like any other if on-screen (its grid is real). No special-casing needed — on-screen nodes have valid grid reads.
- **Numerical:** `double` math in the solver; results cast to `float` for the draw. The edge-arrow already clamps to the screen border, so a mildly-off estimate still yields the right border direction.

## Data flow

`Poe2Atlas.ReadNodes` (GridX/GridY already read) → `BuildAtlasMarks` bakes `GridX/GridY` into `AtlasMark` → SR-10 cull passes them through (`with`) → `RenderContext.AtlasNodes` → `DrawAtlas`: on-screen anchors → `AffineFit2D.TryFit` → off-screen arrowed nodes projected via `Apply` → `DrawEdgeArrow`.

## Testing

- **Core `AffineFit2DTests` (xUnit):** (a) exact recovery — generate anchors from a known affine, assert `TryFit` recovers it and `Apply` reproduces held-out points within ε; (b) over-determined + noise — many noisy anchors, assert the fit is close; (c) degenerate — `< 3` anchors → false; collinear grids → false; (d) `Apply` correctness on hand values.
- **Render wiring:** no unit test (Direct2D); verified in-game.
- **Manual / pre-release verification:** build → hand the reporter the pre-release test build → confirm arrows point toward the actual tracked nodes when off-screen (and toward them as you pan) → then cut the public tag.
- CI: compliance + scrub PASS, full suite green.

## Non-goals

- No distance/hops indicator on the arrow (the existing `Label` stays; distance is a later nice-to-have).
- No new on/off setting — the existing per-tag arrow rules (`AtlasArrowTags`) already control which nodes arrow; re-enabling the off-screen draw restores the prior default behavior.
- No perspective/homography fit (affine is sufficient and matches the atlas projection).
- No change to on-screen rings, route lines, or chevrons.

## Rollout

Ship v0.19.6 built; **pre-release test build to the reporter first** (arrows off-by-nothing if the fit works, ghost-free if it can't fit). On confirmation, tag public → auto-update rolls it out. Default behavior = arrows restored (per existing arrow rules), now accurate.
