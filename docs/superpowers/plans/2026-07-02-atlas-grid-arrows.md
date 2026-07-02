# Atlas Grid-Based Off-Screen Arrows — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring back accurate Atlas off-screen arrows by computing their direction from each node's stable grid coordinate (fit `grid → canvas` from all on-screen nodes on the world thread, project on the render thread).

**Architecture:** Pure Core `AffineFit2D` least-squares solver; the world thread fits `grid → canvas` from all on-screen nodes and publishes the 6-coefficient affine in `RenderContext.AtlasGridFit`; `AtlasMark` forwards `GridX/GridY`; `DrawAtlas` recomputes an off-screen arrowed node's target from its grid via the fit, projects with the existing homography, and draws the edge-arrow.

**Tech Stack:** .NET 10, C#, x64, Windows. Vortice.Direct2D1. xUnit.

## Global Constraints

- **Strictly READ-ONLY of the game** — no new memory reads (grid coords already read); no input, no process writes, no pricing.
- Version → **0.19.6** in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`.
- CI gates must pass: `scripts/compliance-gate.ps1`, `scripts/scrub-strings.ps1 -SelfTest`, full test suite green.
- `MIN_ANCHORS = 4`; the solver requires ≥ 3 mathematically.
- Fit is `grid → canvas` where canvas = mark-space `AtlasCentre(relPos)` (matches how on-screen ring marks are positioned, so the renderer reuses the existing atlas homography for `canvas → screen`).
- All SDD subagents on **Sonnet** (Opus cyber-classifier trips on memory-RE content).
- Branch: `feat/v0.19.6-atlas-grid-arrows`.
- **Verify before public default-on:** after the build passes, hand the reporter a **pre-release test build** to confirm the arrows point true, THEN cut the public tag.

---

### Task 1: Core `AffineFit2D` solver + tests

**Files:**
- Create: `src/POE2Radar.Core/AffineFit2D.cs`
- Test: `tests/POE2Radar.Tests/AffineFit2DTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by Tasks 3 & 4):
  - `readonly record struct Affine(double A, double B, double C, double D, double E, double F)` in namespace `POE2Radar.Core`.
  - `static bool AffineFit2D.TryFit(IReadOnlyList<(float Gx, float Gy, float Sx, float Sy)> anchors, out Affine fit)`
  - `static (float Sx, float Sy) AffineFit2D.Apply(in Affine fit, float gx, float gy)`

- [ ] **Step 1: Write the failing tests**

Create `tests/POE2Radar.Tests/AffineFit2DTests.cs`:

```csharp
using POE2Radar.Core;

public class AffineFit2DTests
{
    // A known affine: sx = 2·gx + 0.5·gy + 100 ; sy = -0.5·gx + 3·gy + 50
    static (float, float, float, float) Anchor(float gx, float gy)
        => (gx, gy, 2f * gx + 0.5f * gy + 100f, -0.5f * gx + 3f * gy + 50f);

    [Fact]
    public void TryFit_recovers_a_known_affine_and_extrapolates_offscreen()
    {
        var anchors = new[] { Anchor(0, 0), Anchor(10, 0), Anchor(0, 10), Anchor(10, 10), Anchor(-5, 7) };
        Assert.True(AffineFit2D.TryFit(anchors, out var fit));
        // Coefficients recovered.
        Assert.Equal(2.0, fit.A, 3); Assert.Equal(0.5, fit.B, 3); Assert.Equal(100.0, fit.C, 3);
        Assert.Equal(-0.5, fit.D, 3); Assert.Equal(3.0, fit.E, 3); Assert.Equal(50.0, fit.F, 3);
        // Apply to a FAR held-out grid coord (the off-screen case) matches the true affine.
        var (sx, sy) = AffineFit2D.Apply(fit, 200f, -150f);
        Assert.Equal(2f * 200f + 0.5f * -150f + 100f, sx, 1);
        Assert.Equal(-0.5f * 200f + 3f * -150f + 50f, sy, 1);
    }

    [Fact]
    public void TryFit_fails_with_fewer_than_three_anchors()
    {
        var two = new[] { Anchor(0, 0), Anchor(1, 1) };
        Assert.False(AffineFit2D.TryFit(two, out _));
        Assert.False(AffineFit2D.TryFit(System.Array.Empty<(float, float, float, float)>(), out _));
    }

    [Fact]
    public void TryFit_fails_on_collinear_grid_points()
    {
        // All grids on the line gy = gx → singular normal matrix → no unique affine.
        var collinear = new[] { Anchor(0, 0), Anchor(1, 1), Anchor(2, 2), Anchor(3, 3), Anchor(4, 4) };
        Assert.False(AffineFit2D.TryFit(collinear, out _));
    }

    [Fact]
    public void Apply_computes_the_affine()
    {
        var fit = new AffineFit2D.Affine(2, 0.5, 100, -0.5, 3, 50);
        var (sx, sy) = AffineFit2D.Apply(fit, 10f, 4f);
        Assert.Equal(122f, sx, 3);   // 2*10 + 0.5*4 + 100
        Assert.Equal(57f, sy, 3);    // -0.5*10 + 3*4 + 50
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~AffineFit2DTests`
Expected: FAIL to compile — `AffineFit2D` does not exist.

- [ ] **Step 3: Implement `AffineFit2D`**

Create `src/POE2Radar.Core/AffineFit2D.cs`:

```csharp
namespace POE2Radar.Core;

/// <summary>
/// Least-squares fit of a 2D affine map <c>(sx,sy) = (A·gx + B·gy + C, D·gx + E·gy + F)</c> from a set of
/// (grid, screen/canvas) sample pairs, plus <see cref="Apply"/> to evaluate it. Used to place OFF-screen
/// atlas nodes: their live position is unreliable once the game culls them, but their integer grid
/// coordinate is stable, so we fit the grid→canvas map from the ON-screen nodes and extrapolate. Pure math;
/// no IO. Both output axes share one 3×3 normal matrix, solved by symmetric inverse.
/// </summary>
public static class AffineFit2D
{
    public readonly record struct Affine(double A, double B, double C, double D, double E, double F);

    /// <summary>Fit from ≥3 non-degenerate anchors. Returns false (and default fit) for &lt;3 anchors, a
    /// singular/collinear set, or a non-finite result. Never throws.</summary>
    public static bool TryFit(IReadOnlyList<(float Gx, float Gy, float Sx, float Sy)> anchors, out Affine fit)
    {
        fit = default;
        if (anchors is null || anchors.Count < 3) return false;

        // Normal matrix N = Σ [gx,gy,1]ᵀ[gx,gy,1] (symmetric 3×3); RHS rx/ry = Σ [gx,gy,1]ᵀ·s.
        double n11 = 0, n12 = 0, n13 = 0, n22 = 0, n23 = 0, n33 = 0;
        double rx1 = 0, rx2 = 0, rx3 = 0, ry1 = 0, ry2 = 0, ry3 = 0;
        foreach (var (gx, gy, sx, sy) in anchors)
        {
            double x = gx, y = gy;
            n11 += x * x; n12 += x * y; n13 += x; n22 += y * y; n23 += y; n33 += 1;
            rx1 += x * sx; rx2 += y * sx; rx3 += sx;
            ry1 += x * sy; ry2 += y * sy; ry3 += sy;
        }

        double det = n11 * (n22 * n33 - n23 * n23)
                   - n12 * (n12 * n33 - n23 * n13)
                   + n13 * (n12 * n23 - n22 * n13);
        if (Math.Abs(det) < 1e-6) return false;   // singular / collinear grids

        // Symmetric inverse (cofactors / det).
        double i11 = (n22 * n33 - n23 * n23) / det, i12 = (n13 * n23 - n12 * n33) / det, i13 = (n12 * n23 - n13 * n22) / det;
        double i22 = (n11 * n33 - n13 * n13) / det, i23 = (n12 * n13 - n11 * n23) / det, i33 = (n11 * n22 - n12 * n12) / det;

        double A = i11 * rx1 + i12 * rx2 + i13 * rx3;
        double B = i12 * rx1 + i22 * rx2 + i23 * rx3;
        double C = i13 * rx1 + i23 * rx2 + i33 * rx3;
        double D = i11 * ry1 + i12 * ry2 + i13 * ry3;
        double E = i12 * ry1 + i22 * ry2 + i23 * ry3;
        double F = i13 * ry1 + i23 * ry2 + i33 * ry3;

        if (!(double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C)
           && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F))) return false;

        fit = new Affine(A, B, C, D, E, F);
        return true;
    }

    /// <summary>Evaluate the fitted affine at a grid coordinate.</summary>
    public static (float Sx, float Sy) Apply(in Affine fit, float gx, float gy)
        => ((float)(fit.A * gx + fit.B * gy + fit.C), (float)(fit.D * gx + fit.E * gy + fit.F));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~AffineFit2DTests`
Expected: PASS (all 4). Then run the full suite once (`dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj`) — nothing broken.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/AffineFit2D.cs tests/POE2Radar.Tests/AffineFit2DTests.cs
git commit -m "feat(atlas): AffineFit2D — least-squares grid→screen affine solver + tests"
```

---

### Task 2: Plumb `GridX/GridY` into `AtlasMark` + `AtlasGridFit` into `RenderContext`

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs`

**Interfaces:**
- Consumes: `POE2Radar.Core.AffineFit2D.Affine` (Task 1).
- Produces (used by Tasks 3 & 4): `AtlasMark.GridX/GridY` (ints) and `RenderContext.AtlasGridFit` (`AffineFit2D.Affine?`).

- [ ] **Step 1: Add `GridX/GridY` to `AtlasMark`**

In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, the `AtlasMark` record (~line 63) currently ends:

```csharp
public readonly record struct AtlasMark(
    float X, float Y, float W, float H,
    bool Selected, bool HasContent, bool Visited, bool Unlocked,
    int Biome, int IconType,
    string? Label = null, string? Color = null,
    bool Arrow = false, bool Nav = false, nint Element = 0,
    IReadOnlyList<string>? ContentIcons = null, bool Visible = false);
```

Append two optional params (keeps every existing positional call working):

```csharp
    IReadOnlyList<string>? ContentIcons = null, bool Visible = false,
    int GridX = 0, int GridY = 0);
```

- [ ] **Step 2: Add `AtlasGridFit` to the `RenderContext` record + the `using`**

Ensure `using POE2Radar.Core;` is present at the top of `RenderContext.cs` (for `AffineFit2D`). Add a parameter to the `RenderContext` record (near the other atlas params, e.g. after `AtlasRouteArrowSpacing`):

```csharp
    // World-thread grid→canvas affine fit (from all on-screen nodes); null when it couldn't be fit.
    // The renderer uses it to place OFF-screen arrowed nodes from their stable grid coord.
    POE2Radar.Core.AffineFit2D.Affine? AtlasGridFit = null,
```

(Place it among the atlas-related optional params so it's an optional named arg; verify it compiles — no non-optional param may follow an optional one.)

- [ ] **Step 3: Build**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)` (ignore MSB3026/3027 copy-lock). RadarApp's existing `new AtlasMark(...)` and `new RenderContext(...)` still compile because the new params are optional.

- [ ] **Step 4: Commit**

```bash
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs
git commit -m "feat(atlas): AtlasMark carries GridX/GridY; RenderContext carries AtlasGridFit"
```

---

### Task 3: World-thread `FitAtlasGrid` + wire grid into marks + publish the fit

**Files:**
- Modify: `src/POE2Radar.Overlay/RadarApp.cs`

**Interfaces:**
- Consumes: `AffineFit2D.TryFit` (Task 1); `AtlasMark.GridX/GridY` + `RenderContext.AtlasGridFit` (Task 2); `AtlasGeometry.AtlasCentre`, `Poe2Atlas.AtlasNodeLive` (existing).
- Produces: `_atlasGridFit` published into `RenderContext.AtlasGridFit`; marks carry grid.

- [ ] **Step 1: Add scratch fields**

Near the other atlas render-scratch fields (e.g. by `_atlasRouteFrame` ~line 93), add:

```csharp
    private readonly List<(float Gx, float Gy, float Sx, float Sy)> _atlasAnchorBuf = new(); // reused fit anchors
    private POE2Radar.Core.AffineFit2D.Affine? _atlasGridFit;   // grid→canvas fit for off-screen arrows (world thread)
    private const int AtlasMinAnchors = 4;
```

- [ ] **Step 2: Add the `FitAtlasGrid` helper**

Add this method to `RadarApp` (isolated; does not touch the freeze/mark-build logic):

```csharp
// Fit a grid→canvas (mark-space, AtlasCentre) affine from every ON-screen node. On-screen nodes have VALID
// relPos (the game only maintains positions for visible elements), so they anchor the fit; off-screen nodes'
// grid coords stay valid and get placed via this fit. Returns null when there aren't enough anchors / it's
// degenerate → the renderer then draws no off-screen arrows (graceful). `pscale` is the atlas draw scale.
private POE2Radar.Core.AffineFit2D.Affine? FitAtlasGrid(IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes, float pscale, float w, float h)
{
    _atlasAnchorBuf.Clear();
    if (w <= 0 || h <= 0) return null;
    foreach (var n in nodes)
    {
        float cx = AtlasGeometry.AtlasCentre(n.X, n.W), cy = AtlasGeometry.AtlasCentre(n.Y, n.H);
        float sx = cx * pscale, sy = cy * pscale;
        if (sx >= 0 && sx <= w && sy >= 0 && sy <= h)     // on-screen ⇒ relPos is trustworthy ⇒ good anchor
            _atlasAnchorBuf.Add(((float)n.GridX, (float)n.GridY, cx, cy));
    }
    return _atlasAnchorBuf.Count >= AtlasMinAnchors
        && POE2Radar.Core.AffineFit2D.TryFit(_atlasAnchorBuf, out var fit)
        ? fit : (POE2Radar.Core.AffineFit2D.Affine?)null;
}
```

- [ ] **Step 3: Call the helper in the atlas world-tick and forward grid into marks**

In the atlas world-tick (the method that builds marks — search for `marks.Add(new AtlasMark(` ~line 3261, and the atlas scale `pscale` computed for the freeze ~line 3109):

1. After the node list is read and `pscale` (`(winH/1600f)·zoom`) + window dims are available, compute the fit once per tick:
   ```csharp
   _atlasGridFit = FitAtlasGrid(nodes, pscale, _window.Width, _window.Height);
   ```
   (If `pscale` from the freeze block isn't in scope at the mark-build site, recompute it identically: `float pscale = (_window.Height > 0 ? _window.Height / 1600f : 0.675f) * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);`)

2. In the `new AtlasMark(...)` call (~line 3261-3267), append the node's grid coords as the new trailing args:
   ```csharp
   marks.Add(new AtlasMark(
       AtlasGeometry.AtlasCentre(n.X, n.W),
       AtlasGeometry.AtlasCentre(n.Y, n.H),
       n.W, n.H,
       isTracked, n.HasContent, n.Visited, n.Unlocked,
       n.Biome, n.IconType, label, color, isArrow, isNav, n.Element,
       contentIcons, n.Visible,
       n.GridX, n.GridY));
   ```

- [ ] **Step 4: Publish the fit into `RenderContext`**

In the `RenderContext(...)` construction (search `new RenderContext(` — the atlas fields include `AtlasNodes: _atlasMarkFrame` ~line 1523), add the named arg:

```csharp
            AtlasGridFit: _atlasGridFit,
```

- [ ] **Step 5: Build + full test suite**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug` → `Build succeeded` / `0 Error(s)`.
Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj` → all pass.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(atlas): world-thread FitAtlasGrid from all on-screen nodes; forward grid into marks + publish fit"
```

---

### Task 4: Render — grid-based off-screen arrows in `DrawAtlas`

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`

**Interfaces:**
- Consumes: `AffineFit2D.Apply` (Task 1); `AtlasMark.GridX/GridY` + `RenderContext.AtlasGridFit` (Tasks 2-3); existing `DrawEdgeArrow`, homography coeffs `h0..h7`, `ccx/ccy`, `col`.
- Produces: accurate off-screen atlas arrows (final consumer).

- [ ] **Step 1: Replace the disabled off-screen branch**

In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`, `DrawAtlas`, the current v0.19.5 off-screen branch reads:

```csharp
            // OFF-SCREEN: skip. Off-screen atlas edge-arrows are DISABLED (v0.19.5). PoE2 stops updating a
            // node's RelativePos once the game culls it off-screen, so the position is stale/garbage and the
            // arrow pointed at nothing real ("ghost arrows to nothing"). The codebase already handles this
            // same problem for route chevrons (drawn only when both endpoints are on-screen). On-screen node
            // rings below are unaffected. A grid-based reliable-direction version (using each node's stable
            // GridX/GridY instead of the unreliable off-screen RelativePos) is the planned follow-up.
            if (!onScreen) continue;
```

Replace it with the grid-based arrow (v0.19.6):

```csharp
            // OFF-SCREEN: draw an edge-arrow using the node's STABLE grid coordinate (its live off-screen
            // relPos is garbage — PoE2 stops updating culled elements). The world thread fit grid→canvas from
            // all on-screen nodes (ctx.AtlasGridFit); we evaluate it at this node's grid, then project
            // canvas→screen with the SAME homography as the rings. Null fit (too few anchors) ⇒ no arrow.
            if (!onScreen)
            {
                if (n.Arrow && ctx.AtlasGridFit is { } gf)
                {
                    var (cx, cy) = POE2Radar.Core.AffineFit2D.Apply(gf, n.GridX, n.GridY);
                    var aw = h6 * cx + h7 * cy + 1f;
                    if (MathF.Abs(aw) >= 1e-6f)
                        DrawEdgeArrow(rt, (h0 * cx + h1 * cy + h2) / aw, (h3 * cx + h4 * cy + h5) / aw,
                                      ccx, ccy, W, H, col, n.Label);
                }
                continue;
            }
```

(Confirm `using POE2Radar.Core;` is present in `OverlayRenderer.cs`, or use the fully-qualified `POE2Radar.Core.AffineFit2D` as written.)

- [ ] **Step 2: Build**

Run: `dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug`
Expected: `Build succeeded`, `0 Error(s)` (ignore MSB3026/3027). No unit test (Direct2D render); correctness is verified in-game via the pre-release build (Task 5).

- [ ] **Step 3: Commit**

```bash
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(atlas): grid-based off-screen arrows in DrawAtlas (accurate direction from stable grid)"
```

---

### Task 5: Integration + version + changelog + gates

**Files:**
- Modify: `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` (`<Version>0.19.6</Version>`), `CHANGELOG.md`

- [ ] **Step 1: Version bump**

In `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj`, change `<Version>0.19.5</Version>` to:

```xml
    <Version>0.19.6</Version>
```

- [ ] **Step 2: CHANGELOG entry**

Add the top entry in `CHANGELOG.md`:

```markdown
## [0.19.6] — 2026-07-02
### Added
- 🧭 **Atlas arrows are back — and accurate.** Off-screen tracked maps/Citadels get a border arrow again, but now the direction is computed from each node's **stable grid coordinate** (fit from the nodes currently on-screen) instead of the position PoE2 stops updating once a node scrolls off-screen. No more ghost arrows — they point at the real thing. On-screen rings and route lines are unchanged.
```

- [ ] **Step 3: Full build + tests + gates**

Run: `dotnet build -c Debug` → `Build succeeded` / `0 Error(s)`.
Run: `dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj` → all pass (existing + AffineFit2DTests).
Run: `powershell -File scripts/compliance-gate.ps1` → `PASS`.
Run: `powershell -File scripts/scrub-strings.ps1 -SelfTest` → `PASSED`.

- [ ] **Step 4: Cross-task consistency check**

Verify by reading:
- `AffineFit2D.Affine` / `TryFit` / `Apply` signatures match across Task 1 (def) and Tasks 3-4 (call sites).
- `AtlasMark` trailing params `GridX, GridY` and the `new AtlasMark(...)` call pass `n.GridX, n.GridY` in that order.
- `RenderContext.AtlasGridFit` is published (`AtlasGridFit: _atlasGridFit`) and consumed (`ctx.AtlasGridFit`).
- The fit is `grid → canvas` (anchors use `AtlasCentre`), and `DrawAtlas` applies `canvas → screen` via the same `h0..h7` homography used for rings — consistent spaces.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/POE2Radar.Overlay.csproj CHANGELOG.md
git commit -m "chore(release): v0.19.6 — atlas grid-based off-screen arrows; version + changelog"
```

- [ ] **Step 6: Whole-branch review + pre-release verification**

Dispatch a final review (compliance intact / read-only; the fit math + coordinate-space consistency grid→canvas→screen; the freeze/mark-build changes are additive and race-free; `AtlasGridFit` null-path draws nothing). Then **build the publish exe and hand the reporter a pre-release test build** (like the `atlas-diag` flow) to confirm arrows point at the real off-screen nodes as they pan — BEFORE cutting the public `v0.19.6` tag.

---

## Self-Review (plan author)

**Spec coverage:** Core `AffineFit2D` + tests → T1. `AtlasMark` grid + `RenderContext.AtlasGridFit` → T2. World-thread `FitAtlasGrid` from all on-screen nodes + forward grid + publish → T3. `DrawAtlas` grid-based arrow → T4. Version/changelog/gates/verify → T5. All spec sections covered.

**Placeholder scan:** No TBD/TODO; complete code in every code step.

**Type consistency:** `AffineFit2D.Affine(A,B,C,D,E,F)`, `TryFit(IReadOnlyList<(float,float,float,float)>, out Affine)`, `Apply(in Affine, float, float)` identical across T1/T3/T4. `AtlasMark(..., int GridX=0, int GridY=0)` matches the `new AtlasMark(..., n.GridX, n.GridY)` call. `RenderContext.AtlasGridFit` (`Affine?`) published in T3, read in T4. `AtlasMinAnchors=4`. Consistent.
