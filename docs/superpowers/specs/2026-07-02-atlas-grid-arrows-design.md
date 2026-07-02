# Atlas Grid-Based Off-Screen Arrows — Design

**Date:** 2026-07-02
**Status:** approved (design), pending spec review
**Release:** v0.19.6 (finishes the atlas-arrow saga: v0.19.5 disabled off-screen arrows; this brings them back, accurate)

## Goal

Bring back the Atlas off-screen edge-arrows (point me toward my tracked Citadels/maps) **working correctly**, by deriving each arrow's direction from the target node's **stable grid coordinate** instead of the game's unreliable off-screen position.

## Background (why the old ones were "ghosts")

PoE2 stops updating a UI element's `RelativePos` once the game culls it off-screen, so an off-screen node's read position is stale/garbage (the diagnostic showed swings to ±1.8 M). The old edge-arrows pointed at that garbage → "arrows to nothing." v0.19.5 disabled them. But each node also carries a **grid coordinate** (`AtlasNode.GridPos +0x320` → `AtlasNodeLive.GridX/GridY`) that is **stable whether or not the node is on-screen** (it's an integer on the node's data object, not the layout engine). On-screen nodes give us `(grid → screen)` sample pairs; from those we can fit a transform and use it to place any off-screen node's target reliably.

## Architecture (2–3 sentences)

A pure, unit-tested Core solver (`AffineFit2D`) fits a 2D affine `canvas = M·grid + b` by least squares. The **world thread** (which reads ALL atlas nodes, not just the tracked ones) collects every on-screen node as a `(grid, canvas)` anchor and fits the affine once per tick, publishing just the 6 coefficients in `RenderContext.AtlasGridFit`. `AtlasMark` forwards each node's `GridX/GridY`. In `OverlayRenderer.DrawAtlas`, an **off-screen arrowed** node's canvas position is recomputed from its grid via the fit (`grid → canvas`), projected `canvas → screen` with the existing atlas homography, and the existing `DrawEdgeArrow` points there.

**Why the world thread fits (not the renderer):** `DrawAtlas` only receives the *tracked* marks (Citadels etc.), which are sparse — usually too few on-screen to fit against. The world thread sees all ~600 nodes, so it has plenty of on-screen anchors. Only the 6-coefficient affine crosses to the render thread (tiny, no per-frame node-list copy).

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

### 2. Overlay plumbing — carry grid into the mark + the fit into RenderContext

- `src/POE2Radar.Overlay/Overlay/RenderContext.cs`:
  - Add `int GridX = 0, int GridY = 0` to `AtlasMark` (append as optional params — no other call site breaks).
  - Add `AffineFit2D.Affine? AtlasGridFit = null` to the `RenderContext` record (the world-thread `grid → canvas` fit, or null when it couldn't be fit).
- `src/POE2Radar.Overlay/RadarApp.cs`:
  - `BuildAtlasMarks` (~3261): pass `n.GridX, n.GridY` into `new AtlasMark(...)`. The SR-10 cull's `m with { X=…, Y=… }` (~1388) preserves the new fields (record `with`), so they reach the renderer unchanged.
  - Publish the fit into the `RenderContext` (`AtlasGridFit: _atlasGridFit`).

### 3. World thread — fit `grid → canvas` from all on-screen nodes

`src/POE2Radar.Overlay/RadarApp.cs` — a small **isolated, additive** helper (does NOT modify the fragile freeze / mark-build logic), called in the atlas world-tick where the node list and the atlas scale `pscale = (winH/1600)·zoom` are known:

- `Affine? FitAtlasGrid(IReadOnlyList<AtlasNodeLive> nodes, float pscale, float w, float h)`:
  - Iterate `nodes`; for each, compute its mark-space canvas centre `cx = AtlasCentre(n.X, n.W)`, `cy = AtlasCentre(n.Y, n.H)` and a rough screen `sx = cx·pscale, sy = cy·pscale`. If `sx,sy` are within `[0,w]×[0,h]` (a generous on-screen test — on-screen nodes have VALID relPos), add anchor `(n.GridX, n.GridY, cx, cy)` to a reused scratch list.
  - `return AffineFit2D.TryFit(anchors, out var fit) && anchors.Count >= MIN_ANCHORS ? fit : (Affine?)null;` (`MIN_ANCHORS = 4`).
  - Store into `_atlasGridFit` (a field) for `RenderContext`. Reuse a scratch anchor list (field) — no per-tick allocation.
- Note: the fit is `grid → canvas` (canvas = mark-space `AtlasCentre(relPos)`), matching how on-screen ring marks are positioned, so the renderer can reuse the existing homography for `canvas → screen`.

### 4. Render — grid-based arrow direction in `DrawAtlas`

`src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (`DrawAtlas`, ~306-382) — replace v0.19.5's `if (!onScreen) continue;` off-screen branch with:

```csharp
if (!onScreen)
{
    // Grid-based arrow: recompute the target from its STABLE grid coord (off-screen relPos is garbage).
    if (n.Arrow && ctx.AtlasGridFit is { } gf)
    {
        var (cx, cy) = AffineFit2D.Apply(gf, n.GridX, n.GridY);        // grid → canvas (world-thread fit)
        var aw = h6 * cx + h7 * cy + 1f;                               // canvas → screen (same homography)
        if (MathF.Abs(aw) >= 1e-6f)
            DrawEdgeArrow(rt, (h0*cx + h1*cy + h2) / aw, (h3*cx + h4*cy + h5) / aw, ccx, ccy, W, H, col, n.Label);
    }
    continue;
}
```

On-screen rings, route lines, and chevrons are untouched. If `AtlasGridFit` is null (fit failed), no off-screen arrows draw (graceful — same as v0.19.5).

### Edge cases

- **< MIN_ANCHORS on-screen** (panned to a sparse/empty area, or window dims not yet set): `FitAtlasGrid` returns null → `AtlasGridFit` null → no off-screen arrows that tick. Acceptable (rare; nothing to anchor against anyway).
- **Degenerate/collinear anchors:** `det(N) ≈ 0` → `TryFit` false → null → no arrows.
- **Grid (0,0):** used as an anchor like any other if on-screen (its grid is real). On-screen nodes have valid grid reads.
- **Numerical:** `double` math in the solver; result cast to `float` at draw. `DrawEdgeArrow` clamps to the screen border, so a mildly-off estimate still yields the right border direction.
- **Off-screen node with garbage relPos that projects on-screen:** its ring may still mis-draw (pre-existing, separate from arrows); not addressed here.

## Data flow

`Poe2Atlas.ReadNodes` (GridX/GridY + relPos already read) → **world thread**: `FitAtlasGrid(nodes, pscale, w, h)` collects all on-screen nodes as `(grid, canvas)` anchors → `AffineFit2D.TryFit` → `_atlasGridFit` (`Affine?`); `BuildAtlasMarks` forwards `GridX/GridY` into `AtlasMark` → published in `RenderContext` (`AtlasNodes` + `AtlasGridFit`). **Render thread** `DrawAtlas`: for an off-screen arrowed mark, `AffineFit2D.Apply(AtlasGridFit, GridX, GridY)` → canvas → existing homography → screen → `DrawEdgeArrow`.

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
