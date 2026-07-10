# POE2GPS Threshold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two-track drop bundling 3 surgical upstream Sikaka/POE2Radar sync items (v0.16.6 atlas DrawBitmap fix + WaygateDevice display rule + v0.15.3 monolith collapse) with the XP/hour Session HUD chip that Campaign Probe's `Poe2Live.PlayerExperience` accessor unblocked (closes Long List #34 / PMS-6).

**Architecture:** Atlas DrawBitmap fix is one-line pattern-matched. WaygateDevice rule adds `BuiltInTileRules()` static + AppliedMigrations entry with defensive `SettingsMigrator.Map`. Monolith collapse ports caret+hit-rect ONLY (preserves `ctx.MonolithsTop` pre-sort/cap-to-6). XP HUD chip: new row in existing SessionHud panel, ring buffer inside `SessionTracker` (piggybacks 5 s cadence), reuses existing embedded `xp_curve.json` (NO duplicate), opt-in default OFF, zone-crossing survives, town-freeze reuses `ExcludeTownsFromPace`, zero-cost-when-off gate in `RadarApp.WorldTick`.

**Tech Stack:** C# / .NET 8+ / net10.0-windows / Direct2D + browser Canvas 2D + SSE (no new SSE keys).

## Global Constraints

- **Zero memory writes.** Every new memory access read-only over PlayerInventories chain (`Poe2Live.PlayerExperience` shipped in Campaign Probe).
- **Zero-cost-when-off (XP HUD).** `_live.PlayerExperience` NOT called when `!ShowXpRate`. Gated in `RadarApp.WorldTick`. Spy test locks.
- **XP curve DEDUP (CRITICAL).** Reuse existing `xp_curve.json` at `Campaign/Guide/Data/poe2/`. DO NOT create `Poe2XpCurve.cs` with a duplicate `static long[]`.
- **`SessionTracker.Update` as 9-arg OVERLOAD.** NOT a signature change. `currentXp=0` handled as skip append + re-emit prior rate.
- **AppliedMigrations defensive Map entry** for `BuiltInTileRulesSeeded` legacy bool.
- **Monolith collapse preserves `ctx.MonolithsTop` pre-sort/cap-to-6.** DO NOT port upstream's inline `OrderByDescending+Take+ToList`.
- **DrawBitmap fix ports by PATTERN match** on `rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH))` — NOT line-numbered.
- **Waygate double-marker guard.** Task 3 MUST include an explicit `DisplayRules_WaygateDeviceMatches_ExactlyOneMarker` test.
- **No upstream-repo-name leaks** (Sikaka / GameHelper / upstream repo names) into public surfaces (README, CHANGELOG, Discord post, in-app strings). Pre-tag grep gate covers.
- **No `TODO`/`FIXME`/`HACK`/`XXX` in new code.**
- **No `superpowers/`, `.superpowers/`, `docs/superpowers/` paths in code.**
- **v0.20 wire-format additive-only.** No new SSE keys added.
- **One clean commit per task.**
- **All tests via `dotnet test` from repo root.** New tests live under `tests/POE2Radar.Tests/...`.

## Canonical Interface Map (AUTHORITATIVE — 6 consistency findings reconciled)

Local `Interfaces:` blocks in per-task sections below may drift from these canonical shapes. **The map here wins.** Implementers reconcile at write-time.

### Task 2 → Task 3: `DisplayRules` surface (finding #1)

Task 2 MUST expose (in addition to `BuiltInTileRules()`):

```csharp
public static class DisplayRules
{
    // Full built-in tile-rule seed set — pure static, no side effects.
    public static IReadOnlyList<TileRule> BuiltInTileRules();

    // Idempotent seed: if AppliedMigrations does not contain "built_in_tile_rules_v1",
    // dedup-Replace the built-in rules into settings and append the migration key.
    // Task 3's tests call this to verify one-shot semantics + no double-marker on re-run.
    public static void SeedBuiltInTileRulesIfNeeded(RadarSettings settings);
}
```

Task 3's Consumes MUST reference `SeedBuiltInTileRulesIfNeeded` from Task 2 (not just `BuiltInTileRules()`).

### Task 6 → Task 8: `SessionStats` XP fields (finding #2)

Task 6 MUST expose these four fields on the `SessionStats` return record of the 9-arg `Update` overload — Task 8's renderer reads them idempotently to build the HUD row without a second tracker query:

```csharp
public readonly record struct SessionStats(
    // ...existing fields per Task 6...
    float XpPerHour,        // Rate computed inside the ring buffer, 0f when window still filling AND no session fallback.
    long CurrentXp,         // Snapshot at this Update call — 0 when player component unresolved (skip-append path).
    long SessionXpDelta,    // Cumulative delta since Reset(now) — used for the fallback rate when ring is still filling.
    bool RingFilling);      // True until the ring has one full window; renderer splits the row to two lines while true.
```

Task 8's D1 line-cache comparand extends by `sess.XpPerHour, sess.CurrentXp, sess.SessionXpDelta, sess.RingFilling` — cache-key drift check.

### Task 6 → Task 9: `SessionTracker.Update` 9-arg overload signature (finding #3)

The canonical 9-arg overload signature IS:

```csharp
public SessionStats Update(
    uint areaHash,
    string areaCode,
    int areaLevel,
    int playerLevel,
    float hpPct,
    long nowTicks,
    bool excludeTowns,
    bool isTown,
    long currentXp);
```

Task 9's reflection smoke test MUST look up the `MethodInfo` by `Type[]` matching this shape — NOT by the shorter kills/deaths/mapsFinished signature Task 9's brief mistakenly copied.

### Task 3 explicit test (finding #4 — Waygate double-marker guard, spec §8 non-negotiable)

Task 3 MUST include a `DisplayRules_WaygateDeviceMatches_ExactlyOneMarker` test in `DisplayRulesWaygateTests.cs`:

- Construct a `Poe2Live.EntityDot` with Metadata containing `"WaygateDevice"`.
- Call `Resolve()`.
- Assert exactly ONE non-null `DisplayRule` returned.
- Enumerate seeded rules and assert NO other rule also matches.

This is the guard for the future R5 Waygate atlas-landmark family port (spec §8 risk 1). Without this test, a future port that adds a second Waygate rule ships silently as a double-marker regression.

### Task 4 explicit non-regression line (finding #5 — `ctx.MonolithsTop` preserve)

Task 4's Edit steps MUST anchor on: **the caret + hit-rect + `PanelCollapsed` gate only**. The shipped POE2GPS path pre-sorts `ctx.MonolithsTop` upstream of this render callsite and caps at 6 there. The Edit steps must NOT touch any `OrderByDescending`/`Take(6)`/`ToList` enumeration in the render callsite. Test asserts row count when collapsed=true; does NOT assert the sort order (that's tested elsewhere).

---

## Per-task briefs

> **Executor note:** Each task section below preserves the original TDD code as authored. Where local `Interfaces:` blocks drift from the canonical shapes above (Task 2 missing `SeedBuiltInTileRulesIfNeeded`, Task 6 missing the 4 `SessionStats` XP fields, Task 9 mis-shaped `Update` signature), the **Canonical Interface Map above is authoritative** — reconcile the type names when you write them. The consistency check applied 6 findings; use the map.

---

### Task 1: THR-DRAW-FIX — Atlas content-icon DrawBitmap destination-rect fix

**Files:**
- Create: `tests/POE2Radar.Tests/Overlay/DrawAtlasContentIconsRectTests.cs`
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs:401-419` (`DrawAtlasContentIcons` method — extract pure static `ComputeAtlasContentIconDestRect` helper and route line 416 through it)
- Test: `tests/POE2Radar.Tests/Overlay/DrawAtlasContentIconsRectTests.cs`

**Interfaces:**
- Consumes: (none — first task in Threshold, no upstream task deps)
- Produces:
  - `internal static Vortice.Mathematics.Rect OverlayRenderer.ComputeAtlasContentIconDestRect(float ix, float iy, float iconH)` — pure helper returning the destination rect for a single content icon cell. Square, LTRB form, origin-aligned. Consumed by no other Threshold task (locked-invariant helper for this task's regression test only).

**Non-negotiables baked in:**
- Zero memory writes (no `MemoryReader.Write*` / `Marshal.Write*` / new offset writes).
- Pattern-match port on `rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH))` — NOT a line-numbered patch.
- Zero references to upstream repo names (no "Sikaka", "GameHelper", no upstream commit hash) in source or test.
- Zero `TODO`/`FIXME`/`HACK`/`XXX` in new code.
- No `superpowers/` path in code.

---

- [ ] **Step 1: Write the failing test.**

Create `tests/POE2Radar.Tests/Overlay/DrawAtlasContentIconsRectTests.cs`:

```csharp
using Vortice.Mathematics;
using Xunit;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// Locks the destination-rect math for the atlas content-icon row on fogged nodes.
/// The rect MUST be square (width == height == iconH) and origin-aligned
/// (left == ix, top == iy, right == ix + iconH, bottom == iy + iconH).
/// Regression guard for the pattern
///   new Rect(ix, iy, ix + iconH, iy + iconH)
/// where Vortice.Mathematics.Rect is LTRB. Any drift to Rect(ix, iy, iconH, iconH)
/// or an asymmetric width/height would silently mis-place icons at high atlas zoom.
/// </summary>
public class DrawAtlasContentIconsRectTests
{
    [Fact]
    public void ComputeAtlasContentIconDestRect_IsSquareAndOriginAligned()
    {
        // Synthetic icon at a known coord, iconH = 24 px.
        const float ix = 137.5f;
        const float iy = 42.0f;
        const float iconH = 24f;

        var rect = POE2Radar.Overlay.OverlayRenderer
            .ComputeAtlasContentIconDestRect(ix, iy, iconH);

        Assert.Equal(ix,          rect.Left,   3);
        Assert.Equal(iy,          rect.Top,    3);
        Assert.Equal(ix + iconH,  rect.Right,  3);
        Assert.Equal(iy + iconH,  rect.Bottom, 3);
        // Square: width == height == iconH.
        Assert.Equal(iconH, rect.Right  - rect.Left, 3);
        Assert.Equal(iconH, rect.Bottom - rect.Top,  3);
    }

    [Theory]
    [InlineData(0f,      0f,      6f)]
    [InlineData(-50f,    100f,    12f)]
    [InlineData(1024.9f, 2048.1f, 32.5f)]
    public void ComputeAtlasContentIconDestRect_SquareForVariedInputs(float ix, float iy, float iconH)
    {
        var rect = POE2Radar.Overlay.OverlayRenderer
            .ComputeAtlasContentIconDestRect(ix, iy, iconH);

        Assert.Equal(iconH, rect.Right  - rect.Left, 3);
        Assert.Equal(iconH, rect.Bottom - rect.Top,  3);
        Assert.Equal(ix, rect.Left, 3);
        Assert.Equal(iy, rect.Top,  3);
    }
}
```

- [ ] **Step 2: Run the test — expect FAIL (helper does not exist yet).**

```
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~DrawAtlasContentIconsRectTests
```

Expected: compile error `CS0117: 'OverlayRenderer' does not contain a definition for 'ComputeAtlasContentIconDestRect'`.

- [ ] **Step 3: Add the pure static helper and route the render call through it.**

In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`, locate `DrawAtlasContentIcons` (starts near line 401). Pattern-match on the existing draw line and rewrite it through the helper.

Edit the block whose current text is:

```csharp
    private void DrawAtlasContentIcons(ID2D1RenderTarget rt, IReadOnlyList<string> basenames, float cx, float topY, float iconH)
    {
        if (iconH < 6f) iconH = 6f;
        var cnt = 0;
        foreach (var bn in basenames) if (_atlasIcons!.Get(rt, bn) != null) cnt++;
        if (cnt == 0) return;
        const float gap = 3f;
        float totalW = cnt * iconH + (cnt - 1) * gap;
        float ix = cx - totalW * 0.5f;
        float iy = topY - iconH - 5f;
        rt.FillRectangle(new Vortice.RawRectF(ix - 3f, iy - 2f, ix + totalW + 3f, iy + iconH + 2f), _bPanel!);
        foreach (var bn in basenames)
        {
            var bmp = _atlasIcons!.Get(rt, bn);
            if (bmp == null) continue;
            rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH));
            ix += iconH + gap;
        }
    }
```

to:

```csharp
    private void DrawAtlasContentIcons(ID2D1RenderTarget rt, IReadOnlyList<string> basenames, float cx, float topY, float iconH)
    {
        if (iconH < 6f) iconH = 6f;
        var cnt = 0;
        foreach (var bn in basenames) if (_atlasIcons!.Get(rt, bn) != null) cnt++;
        if (cnt == 0) return;
        const float gap = 3f;
        float totalW = cnt * iconH + (cnt - 1) * gap;
        float ix = cx - totalW * 0.5f;
        float iy = topY - iconH - 5f;
        rt.FillRectangle(new Vortice.RawRectF(ix - 3f, iy - 2f, ix + totalW + 3f, iy + iconH + 2f), _bPanel!);
        foreach (var bn in basenames)
        {
            var bmp = _atlasIcons!.Get(rt, bn);
            if (bmp == null) continue;
            rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, ComputeAtlasContentIconDestRect(ix, iy, iconH));
            ix += iconH + gap;
        }
    }

    /// <summary>
    /// Destination rect for a single atlas content-icon cell. Square (width == height == <paramref name="iconH"/>),
    /// origin-aligned to (<paramref name="ix"/>, <paramref name="iy"/>). LTRB form — matches
    /// <see cref="Vortice.Mathematics.Rect"/>'s (left, top, right, bottom) constructor. Extracted from
    /// <c>DrawAtlasContentIcons</c> so the row math is unit-lockable — asymmetric width/height or a
    /// non-additive right/bottom silently mis-places icons at high atlas zoom.
    /// </summary>
    internal static Rect ComputeAtlasContentIconDestRect(float ix, float iy, float iconH)
        => new Rect(ix, iy, ix + iconH, iy + iconH);
```

Notes:
- Helper is `internal static` so the xUnit assembly sees it (test project already has `InternalsVisibleTo` where applicable; if not, downgrade to `public static` — same effective surface, still no new public API risk).
- Draw call preserves the exact pattern `new Rect(ix, iy, ix + iconH, iy + iconH)` — semantically identical, now routed through the helper.
- No new memory writes, no new hotkeys, no new panels.

- [ ] **Step 4: Run the test — expect PASS.**

```
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~DrawAtlasContentIconsRectTests
```

Expected: `Passed! - Failed: 0, Passed: 4, Skipped: 0` (1 `[Fact]` + 3 `[Theory]` rows).

- [ ] **Step 5: Repo-wide invariant sweep on the diff.**

```
dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj
git diff -- src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs tests/POE2Radar.Tests/Overlay/DrawAtlasContentIconsRectTests.cs | grep -inE "Sikaka|GameHelper|TODO|FIXME|HACK|XXX|MemoryReader\.Write|Marshal\.Write|SendInput|keybd_event|mouse_event|WriteProcessMemory|VirtualProtect|superpowers/"
```

Expected: build succeeds; grep prints nothing (zero hits). Any hit is a hard stop — fix before proceeding.

- [ ] **Step 6: Full test suite green.**

```
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj
```

Expected: `Failed: 0`. Any pre-existing red is out of scope for this task but must not be caused by the new helper.

- [ ] **Step 7: Commit.**

```
git add src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs tests/POE2Radar.Tests/Overlay/DrawAtlasContentIconsRectTests.cs
git commit -m "THR-DRAW-FIX: pattern-lock atlas content-icon destination rect

Extract ComputeAtlasContentIconDestRect(ix, iy, iconH) as a pure static
helper and route DrawAtlasContentIcons through it. The rect must be
square and origin-aligned (LTRB: ix, iy, ix + iconH, iy + iconH) — any
drift silently mis-places content icons at high atlas zoom.

Adds DrawAtlasContentIconsRectTests as a regression guard."
```

---

### Task 2: WaygateDevice Built-In Tile Display Rule + Migrator Map Entry

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DisplayRules.cs` (append new `BuiltInTileRules()` static after `BuildDefault()` at ~L268)
- Modify: `src/POE2Radar.Overlay/Config/SettingsMigrator.cs:33-46` (append one row to the `Map` tuple array)

**Interfaces:**
- Consumes: none (pure primitive additions)
- Produces:
  - `public static List<DisplayRule> DisplayRules.BuiltInTileRules()` — seed list, one entry: `Name="Waygate", Categories=["Tile"], Match=["WaygateDevice"], Shape="Eye", Color="#00E5FF", Navigable=true, Size=5f, Opacity=1f, Enabled=true`. Task 3 (`THR-WAYGATE-TESTS`) will call this from `RadarApp` behind an `AppliedMigrations.Contains("built_in_tile_rules_v1")` guard and then Replace-with-dedup.
  - `SettingsMigrator.Map` row `("BuiltInTileRulesSeeded", "built_in_tile_rules_v1")` — defensive legacy-bool→key adapter (the fork has no such bool, but this matches the v0.20.1 refactor pattern so any future `Map`-walking code stays uniform).
  - Migration key string `"built_in_tile_rules_v1"` — one-shot idempotent gate for the Waygate seed action.

- [ ] Step 1: Confirm the build is green before touching anything

  ```powershell
  dotnet build C:\Users\minec\Documents\Projects\POE2GPS\POE2Radar.sln -c Debug -nologo
  ```

  Expected: `Build succeeded.` with 0 errors. Any pre-existing failure means we're not on a clean base — stop, investigate main, do NOT proceed.

- [ ] Step 2: Add `BuiltInTileRules()` static to `DisplayRules.cs`

  Insert immediately after the closing `return rules;` and `}` of `BuildDefault(...)` (currently at `src/POE2Radar.Overlay/Web/DisplayRules.cs:267-268`) and before the `// ── internals ───────────────────────────────────────────────────────────` divider at `L270`:

  ```csharp
      /// <summary>
      /// Built-in Tile display rules seeded exactly once via the <c>built_in_tile_rules_v1</c>
      /// migration key in <see cref="RadarSettings.AppliedMigrations"/>. Kept intentionally tiny:
      /// one rule per game-object entity that ships as a first-class navigation target regardless
      /// of the user's <see cref="BuildDefault"/> seed. Currently just <c>WaygateDevice</c> so
      /// end-game waygates render as a distinct <see cref="DisplayRule.Navigable"/> Eye marker.
      /// <para/>
      /// Additive-only: the caller must UNION this list into the existing ruleset (de-duping on
      /// <see cref="DisplayRule.Name"/>) and Replace — never wipe user rules. Idempotent by
      /// construction: the migration-key guard in <c>RadarApp</c> ensures the seed runs at most
      /// once per install. Match term is the exact substring <c>WaygateDevice</c> (no glob, no
      /// path prefix) so a future R5 atlas-landmark port can short-circuit the same entity at
      /// source in <c>Classify()</c> without ambiguity — preventing the double-marker regression
      /// documented in the Threshold spec §2.
      /// </summary>
      public static List<DisplayRule> BuiltInTileRules() => new()
      {
          new DisplayRule
          {
              Enabled = true,
              Name = "Waygate",
              Categories = new() { "Tile" },
              Match = new() { "WaygateDevice" },
              Shape = "Eye",
              Color = "#00E5FF",
              Opacity = 1f,
              Size = 5f,
              Navigable = true,
          },
      };
  ```

- [ ] Step 3: Add the defensive `SettingsMigrator.Map` row

  Edit `src/POE2Radar.Overlay/Config/SettingsMigrator.cs:34-46`. Append the new tuple as the last row of the array, keeping trailing-comma style intact:

  ```csharp
      static readonly (string LegacyKey, string MigrationKey)[] Map = new[]
      {
          ("AtlasRulesInitialized",  "seed:atlas-rules"),
          ("AtlasTargetsSeeded",     "seed:atlas-targets"),
          ("AtlasGroupsSeeded",      "seed:atlas-groups"),
          ("AbyssRuleSeeded",        "seed:abyss-rule"),
          ("IconDefaultsApplied",    "seed:icon-defaults-v1"),
          ("IconDefaultsApplied2",   "seed:icon-defaults-v2"),
          ("RuleCleanupV1",          "seed:rule-cleanup-v1"),
          ("MechanicLabelsV1",       "seed:mechanic-labels-v1"),
          ("GroundDefaultsV2",       "seed:ground-defaults-v2"),
          ("IconSizesV1",            "seed:icon-sizes-v1"),
          ("EntityArrowsSeeded",     "seed:entity-arrows"),
          ("BuiltInTileRulesSeeded", "built_in_tile_rules_v1"),
      };
  ```

  Rationale (comment in code stays as-is — no edits to the `// Enumerate every legacy one-shot bool…` block at L31). The upstream fork has no `BuiltInTileRulesSeeded` bool on `RadarSettings`, but the v0.20.1 refactor pattern requires the row be present so any future code that walks `Map` uniformly (e.g. an audit tool listing every migration that ever shipped) sees the full history.

- [ ] Step 4: Verify the build still compiles

  ```powershell
  dotnet build C:\Users\minec\Documents\Projects\POE2GPS\POE2Radar.sln -c Debug -nologo
  ```

  Expected: `Build succeeded.` with 0 errors, 0 new warnings. If the compiler flags the new `BuiltInTileRules()` return type, confirm `using`s at the top of `DisplayRules.cs` already resolve `List<DisplayRule>` (they do — the file already returns `List<DisplayRule>` from `BuildDefault`).

- [ ] Step 5: Sanity-grep the two additions land where intended

  ```powershell
  Select-String -Path C:\Users\minec\Documents\Projects\POE2GPS\src\POE2Radar.Overlay\Web\DisplayRules.cs -Pattern 'BuiltInTileRules|WaygateDevice|#00E5FF'
  Select-String -Path C:\Users\minec\Documents\Projects\POE2GPS\src\POE2Radar.Overlay\Config\SettingsMigrator.cs -Pattern 'BuiltInTileRulesSeeded|built_in_tile_rules_v1'
  ```

  Expected: `BuiltInTileRules` and `WaygateDevice` and `#00E5FF` each appear exactly once in `DisplayRules.cs`; `BuiltInTileRulesSeeded` and `built_in_tile_rules_v1` each appear exactly once in `SettingsMigrator.cs`. Any zero-hit means the edit didn't land; any >1 hit means a stray paste — investigate before committing.

- [ ] Step 6: Commit

  ```powershell
  git -C C:\Users\minec\Documents\Projects\POE2GPS add src/POE2Radar.Overlay/Web/DisplayRules.cs src/POE2Radar.Overlay/Config/SettingsMigrator.cs
  git -C C:\Users\minec\Documents\Projects\POE2GPS commit -m @'
  Threshold T2: seed WaygateDevice Tile rule + migrator map entry

  Add DisplayRules.BuiltInTileRules() returning a single Tile rule
  for entities named WaygateDevice (Eye, cyan, Navigable) so end-game
  waygates render as a distinct tracked marker.

  Add defensive SettingsMigrator.Map entry mapping legacy
  BuiltInTileRulesSeeded bool to built_in_tile_rules_v1 migration
  key. Fork has no such legacy bool, but the v0.20.1 AppliedMigrations
  refactor pattern requires the row for uniform Map-walking.

  Wiring in RadarApp + idempotency and exactly-one-marker assertions
  land in Task 3 (THR-WAYGATE-TESTS).
  '@
  ```

  Expected: single commit on the current feature branch, both files staged, no other paths touched (no tests, no RadarApp edit, no doc churn).

---

### Task 3: THR-WAYGATE-TESTS — Idempotent migration + exactly-one-marker assertions

**Files:**
- Create: `tests/POE2Radar.Tests/Config/BuiltInTileRulesMigrationTests.cs`
- Create: `tests/POE2Radar.Tests/Web/DisplayRulesWaygateTests.cs`
- Modify: (none — pure test task, csproj already globs `*.cs` under project dir)
- Test: `tests/POE2Radar.Tests/Config/BuiltInTileRulesMigrationTests.cs`, `tests/POE2Radar.Tests/Web/DisplayRulesWaygateTests.cs`

**Interfaces:**
- Consumes (from `THR-WAYGATE-RULE`):
  - `DisplayRules.BuiltInTileRules()` → `List<DisplayRule>` (static, returns the seed set — one `WaygateDevice` Tile rule).
  - `DisplayRules.SeedBuiltInTileRulesIfNeeded(RadarSettings settings)` (instance, idempotent — appends missing built-in rules and stamps `"built_in_tile_rules_v1"` into `settings.AppliedMigrations`).
  - `SettingsMigrator.Map` extended with `("BuiltInTileRulesSeeded", "built_in_tile_rules_v1")` — defensive legacy-bool → migration-key entry per spec §2 non-negotiable.
  - `DisplayRule.Match : List<string>`, `DisplayRules.Resolve(Poe2Live.EntityDot) : DisplayRule?`, `DisplayRules.Replace(IEnumerable<DisplayRule>) : void`, `DisplayRules.All : IReadOnlyList<DisplayRule>`.
  - `Poe2Live.EntityDot` positional record struct (see signature above).
- Produces: (none — assertions only; downstream tasks do not depend on this task's symbols).

**Spec anchors:** §2 Waygate double-marker · §2 `AppliedMigrations` defensive Map entry · §4.2 Tests bullet list · §8 Risk #1.

---

- [ ] **Step 1: Write the failing migration test** — creates `tests/POE2Radar.Tests/Config/BuiltInTileRulesMigrationTests.cs`. Covers (a) `SeedBuiltInTileRulesIfNeeded` is idempotent on a second boot (no duplicate rule, no duplicate `built_in_tile_rules_v1` key), and (b) the defensive `SettingsMigrator.Map` entry folds a legacy `builtInTileRulesSeeded:true` json field into the migration key so a future refactor asking "was this migration ever run" folds uniformly. Uses a temp dir for the `DisplayRules` file so no user config gets touched.

```csharp
using System.IO;
using System.Text.Json;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// Threshold §4.2 — the WaygateDevice built-in Tile display rule is seeded exactly once
/// via <see cref="DisplayRules.SeedBuiltInTileRulesIfNeeded"/>, guarded by the
/// "built_in_tile_rules_v1" entry in <see cref="RadarSettings.AppliedMigrations"/>.
/// A second boot must NOT re-append the rule and must NOT double-stamp the key.
/// The defensive <see cref="SettingsMigrator.Map"/> entry (BuiltInTileRulesSeeded →
/// built_in_tile_rules_v1) also folds a hand-edited legacy bool into the same key so
/// any future "was this migration ever run" refactor sees a single source of truth.
/// </summary>
public class BuiltInTileRulesMigrationTests
{
    [Fact]
    public void SettingsMigrator_BuiltInTileRulesV1_SeedsWaygateRuleOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "poe2gps-tile-seed-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new RadarSettings();
            var rulesPath = Path.Combine(dir, "display_rules.json");
            var rules = new DisplayRules(rulesPath);

            // First boot: seeds the WaygateDevice rule + stamps the migration key.
            rules.SeedBuiltInTileRulesIfNeeded(settings);
            var afterFirstCount = rules.All.Count;
            var waygateCountFirst = rules.All.Count(r => r.Match.Contains("WaygateDevice"));
            Assert.Contains("built_in_tile_rules_v1", settings.AppliedMigrations);
            Assert.Equal(1, waygateCountFirst);
            Assert.True(afterFirstCount >= 1);

            // Second boot on the SAME settings + rules (idempotent guard): rule count
            // unchanged, migration key present exactly once, waygate rule present exactly once.
            rules.SeedBuiltInTileRulesIfNeeded(settings);
            Assert.Equal(afterFirstCount, rules.All.Count);
            Assert.Single(settings.AppliedMigrations.FindAll(k => k == "built_in_tile_rules_v1"));
            Assert.Equal(1, rules.All.Count(r => r.Match.Contains("WaygateDevice")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Defensive path: even though the fork never shipped a legacy `BuiltInTileRulesSeeded`
    /// bool, the v0.20.1 AppliedMigrations refactor pattern requires a Map entry so a
    /// hand-edited json (or a future upstream sync that surfaces the legacy field) folds
    /// into the same "built_in_tile_rules_v1" key. Feeds a minimal json with the legacy
    /// bool = true and asserts the key lands.
    /// </summary>
    [Fact]
    public void SettingsMigrator_Map_Has_BuiltInTileRulesSeeded_Legacy_Entry()
    {
        var json = "{\"builtInTileRulesSeeded\":true," +
                   "\"probeInstallId\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);
        Assert.Contains("built_in_tile_rules_v1", settings.AppliedMigrations);
    }
}
```

- [ ] **Step 2: Run the migration test — verify it PASSES against Task 2's shipped implementation.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Debug --nologo `
  --filter "FullyQualifiedName~BuiltInTileRulesMigrationTests"
```

Expected: `Passed! - Failed: 0, Passed: 2, Skipped: 0`. If a test fails, the failure IS the signal that Task 2's `SeedBuiltInTileRulesIfNeeded` idempotency guard or the defensive `SettingsMigrator.Map` entry is missing — do NOT patch it here, kick it back to `THR-WAYGATE-RULE`.

- [ ] **Step 3: Write the exactly-one-marker test** — creates `tests/POE2Radar.Tests/Web/DisplayRulesWaygateTests.cs`. Seeds ONLY the built-in tile rules (no `BuildDefault` noise), resolves a synthesized `WaygateDevice`-metadata `EntityDot`, asserts one rule matched with `WaygateDevice` in its `Match` list, removes that rule, and confirms `Resolve` returns null — proving no second rule silently double-marks the same entity today. Bakes the future R5 atlas-landmark port compat (§4.2 double-marker risk): if a future rule ever matches the same entity, this test will fail loudly and the atlas port fixes at source (short-circuits `Classify()` or removes the built-in rule), not this rule.

```csharp
using System.IO;
using System.Linq;
using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

/// <summary>
/// Threshold §4.2 + §8 Risk #1 — the WaygateDevice built-in Tile display rule must produce
/// EXACTLY ONE marker for a WaygateDevice entity. Rendering downstream draws one marker per
/// <see cref="DisplayRules.Resolve"/> hit; the risk is a future R5 atlas-landmark port that
/// stamps a second marker through a different code path (Classify()). This test seeds only
/// the built-in tile rules so the assertion isolates the double-marker risk to the seed
/// set itself — if the count ever climbs to 2 here, the atlas port fixes at source.
/// </summary>
public class DisplayRulesWaygateTests
{
    [Fact]
    public void DisplayRules_WaygateDeviceMatches_ExactlyOneMarker()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "poe2gps-waygate-rules-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new RadarSettings();
            var rules = new DisplayRules(Path.Combine(dir, "display_rules.json"));
            rules.SeedBuiltInTileRulesIfNeeded(settings);

            // Synthesized WaygateDevice entity. Metadata substring "WaygateDevice" is the
            // stable seed-rule matcher; Category=Object (waygates surface as world objects
            // in the shipped classifier). All other fields are neutral defaults so no
            // Rarity/Reaction/Life gate accidentally suppresses the match.
            var dot = new Poe2Live.EntityDot(
                Id: 1u,
                Address: (nint)0,
                Grid: Vector2.Zero,
                World: Vector3.Zero,
                Category: Poe2Live.EntityCategory.Object,
                Metadata: "Metadata/Terrain/Leagues/EndGame/WaygateDevice",
                HpCur: 0,
                HpMax: 0,
                Poi: false,
                Reaction: 0,
                Rarity: Poe2Live.Rarity.NonMonster,
                Opened: false);

            var first = rules.Resolve(dot);
            Assert.NotNull(first);
            Assert.Contains("WaygateDevice", first!.Match);

            // Prove exactly ONE marker: strip the matched rule, verify no other seeded rule
            // in the built-in set also fires. Future R5 atlas-landmark port fix is at source.
            var remaining = rules.All.Where(r => !ReferenceEquals(r, first)).ToList();
            rules.Replace(remaining);
            var second = rules.Resolve(dot);
            Assert.Null(second);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
```

- [ ] **Step 4: Run the exactly-one-marker test — verify it PASSES.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Debug --nologo `
  --filter "FullyQualifiedName~DisplayRulesWaygateTests"
```

Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`. A failure here signals either (a) Task 2's `BuiltInTileRules()` seeded more than one rule matching WaygateDevice, or (b) a default-seed collision — kick back to `THR-WAYGATE-RULE` for source-side fix; do NOT weaken the assertion.

- [ ] **Step 5: Run the whole test project — no regressions elsewhere.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj -c Debug --nologo
```

Expected: `Failed: 0`, total count = prior baseline + 3 (2 in `BuiltInTileRulesMigrationTests` + 1 in `DisplayRulesWaygateTests`). If existing `SettingsMigratorTests` or `ProbeSettingsMigrationTests` now fail because Task 2 added a new `Map` row that changes their expected `AppliedMigrations.Count`, that's a Task 2 fixture-update omission — kick back, don't relax those assertions here.

- [ ] **Step 6: Commit.**

```bash
git add tests/POE2Radar.Tests/Config/BuiltInTileRulesMigrationTests.cs \
        tests/POE2Radar.Tests/Web/DisplayRulesWaygateTests.cs
git commit -m "$(cat <<'EOF'
test(threshold): waygate built-in tile rule — idempotent seed + exactly-one marker

Locks the two Threshold §4.2 assertions for the WaygateDevice display rule:
- SeedBuiltInTileRulesIfNeeded is idempotent across boots (no duplicate rule,
  no duplicate built_in_tile_rules_v1 key in AppliedMigrations).
- Defensive SettingsMigrator.Map entry folds a legacy builtInTileRulesSeeded
  bool into the same migration key.
- WaygateDevice entity resolves to exactly one marker today; future atlas-
  landmark port fixes any double-marker at source, not by weakening this test.
EOF
)"
```

**Non-negotiables baked in:**
- Zero memory writes / zero new offsets touched.
- No `_live.PlayerExperience` calls in this task.
- No duplicate XP curve.
- No hard `SessionTracker.Update` signature change.
- No upstream repo names in test strings or commit body.
- No TODO/FIXME/HACK/XXX in the new code.
- No `superpowers/` paths in code.
- Temp dirs cleaned in `finally` — no user config touched.

---

### Task 4: THR-MONO-COLLAPSE — Click-to-collapse nearby-monolith reward panel

**Files:**
- Create: `src/POE2Radar.Overlay/Overlay/MonolithPanelLayout.cs`
- Create: `tests/POE2Radar.Tests/Overlay/MonolithPanelLayoutTests.cs`
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs:604` (add persisted `PanelCollapsed` to `MonolithSettings` block)
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs:235` (add `MonolithPanelCollapsed` ctx flag next to `ShowMonolithPanel`)
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs:840` (`DrawMonolithPanel`: caret + hit-rect + collapsed short-circuit)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs:1668` (mirror `PanelCollapsed` into ctx)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs:2033` (`OnOverlayClick`: handle `"mono-collapse"` action)

**Interfaces:**
- Consumes: none (Task 4 runs parallel with Tasks 1-3 per spec §6).
- Produces:
  - `MonolithSettings.PanelCollapsed` — `public bool PanelCollapsed { get; set; } = false;` (persisted, opt-in expanded default preserves current shipped UX).
  - `RenderContext.MonolithPanelCollapsed` — `bool MonolithPanelCollapsed = false` (mirrored from settings).
  - `POE2Radar.Overlay.MonolithPanelLayout.CountVisibleRewardRows(IReadOnlyList<MonolithMarker> list, bool collapsed) → int` — pure helper (only consumer today is the test; renderer uses same predicate inline for zero-allocation frame path).
  - `LegendRowRects` action string `"mono-collapse"` (title-bar hit-rect, routed by existing `HitTestWidget` / `OnOverlayClick` chain).

---

- [ ] **Step 1: Write the failing test.** Pure-logic helper — `DrawMonolithPanel` itself calls Direct2D and is not unit-testable, so the collapse gate is factored into a static helper that both the renderer and the test consume. Create `tests/POE2Radar.Tests/Overlay/MonolithPanelLayoutTests.cs`:

```csharp
using System.Collections.Generic;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Overlay;
using Xunit;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Tests.Overlay;

public class MonolithPanelLayoutTests
{
    static MonolithMarker Mk(params double[] rewardEx)
    {
        var rewards = new List<MonolithReward>();
        foreach (var ex in rewardEx)
            rewards.Add(new MonolithReward("r", 1, ex, 0, ""));
        return new MonolithMarker(
            Grid: new NumVec2(0, 0), Holes: 3, IsUnique: false, Collected: false,
            AnchorName: "A", BestEx: rewardEx.Length > 0 ? rewardEx[0] : 0,
            BestName: "r", Color: 0xFFFFFFFFu, Rewards: rewards);
    }

    [Fact]
    public void OverlayRenderer_DrawMonolithPanel_CollapsedStateHidesRewardRows()
    {
        var list = new List<MonolithMarker> { Mk(50, 20, 10), Mk(30, 5) };

        // Expanded: sums (Ex>0 && shown<3) rows per monolith → 3 + 2 = 5.
        Assert.Equal(5, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));

        // Collapsed: title-only panel — zero reward rows drawn.
        Assert.Equal(0, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: true));
    }

    [Fact]
    public void CountVisibleRewardRows_CapsAtThreeRewardsPerMonolith()
    {
        // Preserves DrawMonolithPanel's `shown >= 3` cap per monolith.
        var list = new List<MonolithMarker> { Mk(50, 40, 30, 20, 10) };
        Assert.Equal(3, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));
    }

    [Fact]
    public void CountVisibleRewardRows_SkipsZeroOrNegativeEx()
    {
        // Preserves DrawMonolithPanel's `r.Ex <= 0` skip.
        var list = new List<MonolithMarker> { Mk(50, 0, 10, -1, 5) };
        Assert.Equal(3, MonolithPanelLayout.CountVisibleRewardRows(list, collapsed: false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails.** Expected: compile error `CS0234: The type or namespace name 'MonolithPanelLayout' does not exist in the namespace 'POE2Radar.Overlay.Overlay'`.

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~MonolithPanelLayoutTests" --nologo
```

- [ ] **Step 3: Create the pure-logic helper.** Write `src/POE2Radar.Overlay/Overlay/MonolithPanelLayout.cs` mirroring the exact `Ex>0 && shown<3` predicate already in `DrawMonolithPanel`. Zero allocation — plain int-sum loop.

```csharp
using System.Collections.Generic;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Pure-logic helper for the nearby-monolith reward panel. Extracted from
/// <c>OverlayRenderer.DrawMonolithPanel</c> so the click-to-collapse gate can be unit-tested
/// without a Direct2D render target. The predicate here MUST stay byte-identical to the
/// per-reward gate inside <c>DrawMonolithPanel</c> (<c>r.Ex &gt; 0</c> and <c>shown &lt; 3</c>
/// per monolith) — the two live side-by-side so a divergence trips the test immediately.
/// </summary>
public static class MonolithPanelLayout
{
    /// <summary>
    /// Number of reward rows the monolith panel would draw for <paramref name="list"/>.
    /// Returns 0 when <paramref name="collapsed"/> is true (title row only). The caller is
    /// responsible for the pre-sort / cap-to-6 on <paramref name="list"/> — this helper
    /// preserves whatever ordering it receives (see <c>ctx.MonolithsTop</c> pre-sort in
    /// <c>RadarApp</c>).
    /// </summary>
    public static int CountVisibleRewardRows(IReadOnlyList<MonolithMarker> list, bool collapsed)
    {
        if (collapsed) return 0;
        var total = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var shown = 0;
            var rewards = list[i].Rewards;
            for (var j = 0; j < rewards.Count; j++)
            {
                if (rewards[j].Ex <= 0) continue;
                if (shown >= 3) break;
                shown++;
            }
            total += shown;
        }
        return total;
    }
}
```

- [ ] **Step 4: Run tests — helper alone.** Both facts pass:

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~MonolithPanelLayoutTests" --nologo
```

Expected: `Passed! - Failed: 0, Passed: 3`.

- [ ] **Step 5: Persist the collapse bool on `MonolithSettings`.** Edit `src/POE2Radar.Overlay/Config/RadarSettings.cs` around L604 — add the new property inside the existing `MonolithSettings` sealed class (the spec's `MonolithSettings.cs` reference is a location hint; the type lives in `RadarSettings.cs`):

```csharp
public sealed class MonolithSettings
{
    public bool Enabled { get; set; } = true;
    // Value tiers (best offered reward, Exalted): green >= HighlightMinEx, yellow from 0.6x, neutral below.
    public double HighlightMinEx { get; set; } = 30.0;
    public double MinRewardEx { get; set; } = 1.0;       // hide reward rows below this (panel + dashboard)
    public bool HideCollected { get; set; } = true;      // hide monoliths whose reward was already claimed
    public bool ShowPanel { get; set; } = false;         // the in-overlay nearby-monolith reward panel (off by default; toggle in Settings)
    public bool ShowMapLabel { get; set; } = true;       // draw the value + top-reward label at the icon
    public float PanelMaxDistance { get; set; } = 0f;    // 0 = every monolith in the area; else only within N grid
    // Click-to-collapse state for the in-overlay reward panel: expanded by default so first-run UX
    // is unchanged. Toggled by clicking the title-bar caret; persisted via the standard settings save.
    public bool PanelCollapsed { get; set; } = false;
}
```

- [ ] **Step 6: Wire the ctx flag + renderer.** Two edits.

**6a. `src/POE2Radar.Overlay/Overlay/RenderContext.cs` — add flag right after `ShowMonolithPanel` (L235):**

```csharp
    IReadOnlyList<MonolithMarker>? Monoliths = null,
    bool ShowMonolithPanel = true,
    // Persisted click-to-collapse state for the nearby-monolith reward panel. Collapsed hides all
    // reward rows; only the title row (with caret) renders. Mirrored from MonolithSettings.PanelCollapsed.
    bool MonolithPanelCollapsed = false,
    // Pre-sorted (desc BestEx), capped-to-6 slice of Monoliths for the panel rows - avoids per-frame
    // OrderByDescending(...).Take(6).ToList() in the renderer. Null/empty -> none.
    IReadOnlyList<MonolithMarker>? MonolithsTop = null,
```

**6b. `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` — replace the whole `DrawMonolithPanel` body (L840-873). Preserves `ctx.MonolithsTop` (no inline OrderByDescending+Take+ToList); adds caret glyph in title bar; registers the title rect under action `"mono-collapse"` in `_legendRowRects`; short-circuits reward rows when collapsed:**

```csharp
    private void DrawMonolithPanel(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.ShowMonolithPanel || ctx.Monoliths is not { Count: > 0 } monos) return;
        // Preserve the pre-sorted, capped-to-6 Top list built at world rate in RadarApp.
        // Do NOT inline OrderByDescending+Take+ToList here — it regresses shipped prioritization.
        var list = ctx.MonolithsTop ?? (IReadOnlyList<MonolithMarker>)Array.Empty<MonolithMarker>();
        const float w = 248f, pad = 6f, lineH = 15f, headH = 17f, titleH = 18f;

        var collapsed = ctx.MonolithPanelCollapsed;

        // Height math: title row always drawn; reward rows only when expanded.
        float h = pad * 2f + titleH;
        if (!collapsed)
        {
            foreach (var m in list)
            {
                var rows = 0; foreach (var r in m.Rewards) if (r.Ex > 0 && rows < 3) rows++;
                h += headH + lineH * rows;
            }
        }
        float x = ctx.WindowWidth - w - 10f, y = 90f;
        rt.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), _bPanel!);

        float cy = y + pad;
        var caret = collapsed ? "▸" : "▾"; // triangle-right (collapsed) / triangle-down (expanded)
        rt.DrawText($"{caret} Monoliths ({monos.Count})", _tf!,
            new Rect(x + pad, cy, x + w - pad, cy + titleH), _bText!, DrawTextOptions.Clip);

        // Title-bar hit-rect — routed by RadarApp.HitTestWidget / OnOverlayClick under action "mono-collapse".
        _legendRowRects.Add((new Vortice.RawRectF(x, y, x + w, y + pad + titleH), "mono-collapse"));

        cy += titleH;
        if (collapsed) return; // title-only panel; reward rows suppressed.

        foreach (var m in list)
        {
            _bStyle!.Color = ColorFromU(m.Color);
            var hdr = m.BestEx > 0 ? $"{m.BestEx:F0}ex · {m.AnchorName} {m.Holes}h" : $"{m.AnchorName} {m.Holes}h";
            rt.DrawText(hdr, _tf!, new Rect(x + pad, cy, x + w - pad, cy + headH), _bStyle, DrawTextOptions.Clip);
            cy += headH;
            var shown = 0;
            foreach (var r in m.Rewards)
            {
                if (r.Ex <= 0 || shown >= 3) continue;
                rt.DrawText($"  {r.Ex,4:F0}  {r.Name}", _tf!, new Rect(x + pad, cy, x + w - pad, cy + lineH), _bText!, DrawTextOptions.Clip);
                cy += lineH; shown++;
            }
        }
    }
```

- [ ] **Step 7: Mirror ctx flag + wire click routing in `RadarApp.cs`.** Two edits.

**7a. Around L1668 — pass `PanelCollapsed` into the context:**

```csharp
            Monoliths: monoliths,
            ShowMonolithPanel: _settings.Monoliths.ShowPanel,
            MonolithPanelCollapsed: _settings.Monoliths.PanelCollapsed,
            MonolithsTop: worldFresh && mr.AreaHash == _areaHash ? mr.Top : (IReadOnlyList<MonolithMarker>)Array.Empty<MonolithMarker>(),
```

**7b. Around L2033 — extend `OnOverlayClick` (add branch alongside the shipped `menu-toggle` / `corner:` / `target:` branches):**

```csharp
    private void OnOverlayClick(int clientX, int clientY)
    {
        var action = HitTestWidget((clientX, clientY));
        if (action is null) return;

        if (action == "menu-toggle")
        {
            _navMenuExpanded = !_navMenuExpanded;
        }
        else if (action == "mono-collapse")
        {
            _settings.Monoliths.PanelCollapsed = !_settings.Monoliths.PanelCollapsed;
            _settings.Save();
        }
        else if (action.StartsWith("corner:", StringComparison.Ordinal))
        {
            _settings.NavMenuCorner = action.Substring("corner:".Length);
            _settings.Save();
        }
        else if (action.StartsWith("target:", StringComparison.Ordinal))
        {
            TogglePathTarget(action.Substring("target:".Length));
        }
    }
```

- [ ] **Step 8: Full-suite compile + regression sweep.** Solution builds; the new tests plus all previously green Overlay tests still pass; grep gates on non-negotiables come up empty:

```powershell
dotnet build POE2Radar.sln -c Debug --nologo
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~Overlay|FullyQualifiedName~MonolithPanelLayoutTests" --nologo
# Non-negotiable grep gates (must return zero hits in the touched files):
Select-String -Path src/POE2Radar.Overlay/Overlay/MonolithPanelLayout.cs,src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs,src/POE2Radar.Overlay/Config/RadarSettings.cs,src/POE2Radar.Overlay/RadarApp.cs -Pattern "MemoryReader\.Write|Marshal\.Write|SendInput|keybd_event|mouse_event|WriteProcessMemory|VirtualProtect|Sikaka|GameHelper|superpowers/|TODO|FIXME|HACK|XXX|OrderByDescending"
```

Expected: build succeeds, all filtered tests pass, `Select-String` prints nothing (zero-hits confirms zero memory writes, no upstream repo names, no TODO/FIXME/HACK/XXX in new code, no superpowers/ paths, and no inline `OrderByDescending` regressed into `DrawMonolithPanel`).

- [ ] **Step 9: Commit.**

```powershell
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs src/POE2Radar.Overlay/Overlay/MonolithPanelLayout.cs src/POE2Radar.Overlay/RadarApp.cs tests/POE2Radar.Tests/Overlay/MonolithPanelLayoutTests.cs
git commit -m @'
THR-MONO-COLLAPSE: click-to-collapse nearby-monolith reward panel

- MonolithSettings.PanelCollapsed persisted bool, expanded by default.
- DrawMonolithPanel: caret glyph in title bar; title-only render when collapsed.
- Title-bar hit-rect routed via existing _legendRowRects under "mono-collapse".
- OnOverlayClick toggles PanelCollapsed and saves.
- Preserves ctx.MonolithsTop pre-sort/cap-to-6 (no inline OrderByDescending).
- New MonolithPanelLayout helper for testable row-count semantics.
'@
```

---

### Task 5: PoE2XpCurveLoader — Static Reader Over Existing Embedded xp_curve.json

Static loader for the PoE2 cumulative XP curve. Reuses the embedded resource `POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json` that already ships (Core.csproj:22, referenced by RouteModel.cs:121). Exposes `XpToNextLevel` + `TimeToNextLevel` so the later THR-XP-HUD-RENDER row can print "1.24M  (12m to L86)".

**HARD NON-NEGOTIABLE (spec §2 / task hint):** DO NOT create a `Poe2XpCurve.cs` with a duplicate `static long[]`. The embedded JSON is the single source of truth — a duplicate array silently drifts on any future PoE2 XP rebalance. The loader must `Assembly.GetManifestResourceStream` the existing resource, parse once via `Lazy<long[]>`, and cache.

**Files:**
- Create: `src/POE2Radar.Core/Session/PoE2XpCurveLoader.cs`
- Create: `tests/POE2Radar.Tests/Session/PoE2XpCurveLoaderTests.cs`
- Modify: none — csproj already embeds `xp_curve.json`; no new resources, no new `.csproj` edits.

**Interfaces:**
- Consumes: nothing from earlier THR tasks (leaf, parallel-safe).
- Produces:
  - `PoE2XpCurveLoader.XpToNextLevel(int level, long currentXp) -> long?` — null when `level < 1` OR `level >= 100`; else `cumulative[level] - currentXp` clamped at 0.
  - `PoE2XpCurveLoader.TimeToNextLevel(int level, long currentXp, float xpPerHour) -> TimeSpan?` — null when `xpPerHour <= 0` OR `level >= 100`; else `TimeSpan.FromHours(remaining / xpPerHour)`.
  - `PoE2XpCurveLoader.Cumulative -> long[]` — lazy-loaded 100-entry array; `[0] == 0`, `[99] == 4_250_334_444`.

---

- [ ] **Step 1: Write the failing tests.**

Create `tests/POE2Radar.Tests/Session/PoE2XpCurveLoaderTests.cs` with the file below. Known-value expectations derive from the shipped `xp_curve.json`:
- L20 threshold `cumulative[19] = 843,709`, `cumulative[20] = 1,030,734` → delta `187,025`.
- L50 threshold `cumulative[49] = 54,607,467`, `cumulative[50] = 60,565,335` → delta `5,957,868`.
- L90 threshold `cumulative[89] = 1,934,009,687`, `cumulative[90] = 2,094,900,291` → delta `160,890,604`.
- L99 → L100 boundary `cumulative[99] = 4,250,334,444`.

```csharp
using System;
using POE2Radar.Core.Session;
using Xunit;

namespace POE2Radar.Tests;

public class PoE2XpCurveLoaderTests
{
    [Fact]
    public void Cumulative_LoadsExactlyOneHundredEntries()
    {
        var arr = PoE2XpCurveLoader.Cumulative;
        Assert.Equal(100, arr.Length);
        Assert.Equal(0L, arr[0]);
        Assert.Equal(4_250_334_444L, arr[99]);
    }

    // At the exact L20 threshold, 187,025 XP remain to reach L21.
    [Fact]
    public void XpToNextLevel_L20_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(20, 843_709L);
        Assert.Equal(187_025L, need);
    }

    // At the exact L50 threshold, 5,957,868 XP remain to reach L51.
    [Fact]
    public void XpToNextLevel_L50_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(50, 54_607_467L);
        Assert.Equal(5_957_868L, need);
    }

    // At the exact L90 threshold, 160,890,604 XP remain to reach L91.
    [Fact]
    public void XpToNextLevel_L90_KnownDelta()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(90, 1_934_009_687L);
        Assert.Equal(160_890_604L, need);
    }

    [Fact]
    public void XpToNextLevel_L100_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(100, 4_250_334_444L));
    }

    [Fact]
    public void XpToNextLevel_InvalidLevel_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(0, 100L));
        Assert.Null(PoE2XpCurveLoader.XpToNextLevel(-3, 100L));
    }

    // Overshoot clamps to zero rather than emitting a negative remaining.
    [Fact]
    public void XpToNextLevel_OvershotThreshold_ClampsToZero()
    {
        var need = PoE2XpCurveLoader.XpToNextLevel(20, 2_000_000L);
        Assert.Equal(0L, need);
    }

    // 187,025 XP left at 100,000 XP/h == 1.87025 hours (~1h 52m 12.9s).
    [Fact]
    public void TimeToNextLevel_L20_AtHundredKPerHour()
    {
        var ttn = PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, 100_000f);
        Assert.NotNull(ttn);
        Assert.Equal(TimeSpan.FromHours(187_025.0 / 100_000.0), ttn!.Value);
    }

    [Fact]
    public void TimeToNextLevel_NonPositiveRate_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, 0f));
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(20, 843_709L, -50f));
    }

    [Fact]
    public void TimeToNextLevel_MaxLevel_ReturnsNull()
    {
        Assert.Null(PoE2XpCurveLoader.TimeToNextLevel(100, 5_000_000_000L, 1_000_000f));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (RED).**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~PoE2XpCurveLoaderTests"
```

Expected error: `CS0246: The type or namespace name 'PoE2XpCurveLoader' could not be found` — production type does not exist yet.

- [ ] **Step 3: Write the loader implementation.**

Create `src/POE2Radar.Core/Session/PoE2XpCurveLoader.cs`:

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Session;

// Static reader for the PoE2 cumulative XP curve.
//
// SOURCE OF TRUTH: the embedded resource
//   POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json
// already carried by Core.csproj (see RouteModel.XpCurveResource in
// Campaign/Guide/RouteModel.cs). The JSON is 100 entries indexed 0..99 where
// index (L - 1) is the cumulative XP required to REACH level L. Index 0 == 0.
//
// Do NOT bake the array into C# as a duplicate static long[]. Two sources of
// truth silently drift on any future PoE2 XP rebalance.
public static class PoE2XpCurveLoader
{
    // resource name matches RouteModel.XpCurveResource verbatim so a rename
    // lands in one grep.
    private const string XpCurveResource =
        "POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json";

    private static readonly Lazy<long[]> _cumulative =
        new(LoadCumulative, isThreadSafe: true);

    // 100-entry cumulative curve. Index 0 == L1 threshold (0).
    // Index 99 == L100 threshold (4,250,334,444). Exposed primarily for tests
    // and dashboard diagnostics; runtime consumers should prefer the helpers.
    public static long[] Cumulative => _cumulative.Value;

    // XP still needed to reach (level + 1). Returns null when the player is
    // already at max (level >= 100) or the caller passed a nonsense level.
    // Overshoot (currentXp already past the next threshold) clamps to 0
    // rather than emitting a negative delta.
    public static long? XpToNextLevel(int level, long currentXp)
    {
        if (level < 1 || level >= 100) return null;
        var arr = _cumulative.Value;
        // arr[level] is index (level+1 - 1) == XP required to reach (level+1).
        long threshold = arr[level];
        long remaining = threshold - currentXp;
        return remaining < 0 ? 0 : remaining;
    }

    // Wall-clock estimate to reach next level given a sustained xpPerHour.
    // Null when xpPerHour is non-positive OR the player is at max.
    // Callers gate on ShowXpRate before invoking; no per-tick work here.
    public static TimeSpan? TimeToNextLevel(int level, long currentXp, float xpPerHour)
    {
        if (xpPerHour <= 0f) return null;
        var remaining = XpToNextLevel(level, currentXp);
        if (remaining is null) return null;
        double hours = remaining.Value / (double)xpPerHour;
        return TimeSpan.FromHours(hours);
    }

    private static long[] LoadCumulative()
    {
        var asm = typeof(PoE2XpCurveLoader).Assembly;
        using var s = asm.GetManifestResourceStream(XpCurveResource)
            ?? throw new InvalidOperationException(
                $"embedded resource '{XpCurveResource}' not found");
        using var doc = JsonDocument.Parse(s);
        var cum = doc.RootElement.GetProperty("cumulative");
        int len = cum.GetArrayLength();
        var arr = new long[len];
        for (int i = 0; i < len; i++)
            arr[i] = cum[i].GetInt64();
        return arr;
    }
}
```

- [ ] **Step 4: Run tests to verify GREEN.**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~PoE2XpCurveLoaderTests"
```

Expected: `Passed! - Failed: 0, Passed: 10, Skipped: 0`.

- [ ] **Step 5: Run the pre-tag hygiene grep gates locally.**

Confirm no duplicate-array anti-pattern, no memory writes, no upstream repo leaks, no TODO markers in the new file:

```powershell
Select-String -Path src/POE2Radar.Core/Session/PoE2XpCurveLoader.cs -Pattern 'static\s+long\[\]\s*=|new\s+long\[\]\s*\{|Poe2XpCurve\b|Sikaka|GameHelper|TODO|FIXME|HACK|XXX|WriteProcessMemory|Marshal\.Write|MemoryReader\.Write|SendInput|keybd_event|mouse_event|superpowers/'
```

Expected: no matches. (`Lazy<long[]>` type declaration is fine — the grep pattern excludes it via the required `= ` or `{ ` suffix that a literal array would carry.)

Also run the full solution build to catch analyzer warnings promoted to errors (`TreatWarningsAsErrors=true` in Core.csproj):

```powershell
dotnet build POE2GPS.sln --configuration Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit the loader + tests together.**

```powershell
git add src/POE2Radar.Core/Session/PoE2XpCurveLoader.cs tests/POE2Radar.Tests/Session/PoE2XpCurveLoaderTests.cs
git commit -m @'
Add PoE2XpCurveLoader over existing embedded xp_curve.json

Static reader that lazily parses the already-embedded 100-entry cumulative
curve at Campaign/Guide/Data/poe2/xp_curve.json (resource shared with
RouteModel.XpCurveResource). Exposes XpToNextLevel + TimeToNextLevel so
the upcoming Session HUD XP/hour chip can render "(12m to L86)".

Deliberately does NOT duplicate the array as a static long[] — the JSON
remains the single source of truth so any future PoE2 XP rebalance lands
in one file. Verified against L20/L50/L90 threshold deltas + L100 max
behaviour.
'@
```

Gate reference: **gate-thr-3-xp-curve-loader** — passes when all 10 xUnit facts are green, the hygiene grep returns no matches, and Release build is clean.

---

### Task 6: THR-XP-TRACKER — SessionTracker XP ring buffer + 9-arg Update overload

**Files:**
- Modify: `src/POE2Radar.Core/Session/SessionTracker.cs` (add ring buffer fields, XpPerHour + XpWindowMinutes properties, 9-arg Update overload, extend Reset to clear XP state)
- Create/Test: `tests/POE2Radar.Tests/Session/SessionTrackerXpRingTests.cs`

**Interfaces:**
- Consumes: none new — existing `SessionTracker.Update(uint,string,int,int,float,long,bool,bool)` 8-arg (unchanged) and `Reset(long)` (extended internally, signature stable).
- Produces:
  - `public SessionStats SessionTracker.Update(uint areaHash, string areaCode, int areaLevel, int playerLevel, float hpPct, long nowTicks, bool excludeTowns, bool isTown, long currentXp)` — 9-arg overload; delegates to 8-arg then appends to XP ring
  - `public float SessionTracker.XpPerHour { get; }` — last computed rate (0f until enough samples)
  - `public int SessionTracker.XpWindowMinutes { get; set; }` — default 5, setter clamps to 1..60; on-change resets ring
  - `SessionTracker.Reset(long nowTicks)` — behavior extended to clear XP ring + zero XpPerHour + drop session-XP baseline so next XP-bearing Update re-seeds delta=0

**Non-negotiables baked in (spec §2):**
- No memory writes, no `MemoryReader.Write*`, no `Marshal.Write*` — this file is pure counter state.
- Ring survives zone crossings (the 8-arg Update's zone-change branch is untouched; ring state lives outside it).
- Town frames DO NOT append — reuses the shipped `excludeTowns && isTown` gate; no new knob.
- `currentXp <= 0` → skip append + re-emit prior rate (no NaN, no zero-spike).
- 9-arg is an OVERLOAD not a signature change — every shipped 8-arg caller keeps compiling.
- Reset re-seeds so the first post-reset XP tick reads delta=0 cleanly (no synthetic megaburst).
- No duplicate xp curve array in this file — curve loading is Task 5's concern.
- No TODO/FIXME/HACK/XXX; no `superpowers/` paths; no upstream repo names.

---

- [ ] **Step 1: Write the failing test file**

  Create `tests/POE2Radar.Tests/Session/SessionTrackerXpRingTests.cs`:

  ```csharp
  using POE2Radar.Core.Session;

  // Ring-buffer facet of SessionTracker (Threshold — THR-XP-TRACKER).
  // Locks the 9-arg Update overload, XpPerHour + XpWindowMinutes properties,
  // zone/town semantics, zero-currentXp skip, and Reset re-seed.
  public class SessionTrackerXpRingTests
  {
      private static long T(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

      // 9-arg feeder (alive, non-town by default)
      private static SessionStats Feed(SessionTracker t, long currentXp, long nowTicks,
          uint areaHash = 1, bool excludeTowns = false, bool isTown = false,
          string areaCode = "G1_1")
          => t.Update(areaHash, areaCode, areaLevel: 50, playerLevel: 85,
                      hpPct: 100f, nowTicks: nowTicks,
                      excludeTowns: excludeTowns, isTown: isTown,
                      currentXp: currentXp);

      [Fact]
      public void XpWindowMinutes_DefaultsToFive()
      {
          var t = new SessionTracker();
          Assert.Equal(5, t.XpWindowMinutes);
      }

      [Fact]
      public void XpWindowMinutes_ClampsSetterToOneThroughSixty()
      {
          var t = new SessionTracker();
          t.XpWindowMinutes = 0;
          Assert.Equal(1, t.XpWindowMinutes);
          t.XpWindowMinutes = 999;
          Assert.Equal(60, t.XpWindowMinutes);
      }

      [Fact]
      public void XpRing_FallbackWhileFilling_UsesSessionDeltaOverSessionHours()
      {
          var t = new SessionTracker { XpWindowMinutes = 5 };  // slots = max(12, 60) = 60
          Feed(t, currentXp: 1_000_000, nowTicks: T(0));       // seeds baseline
          Feed(t, currentXp: 1_100_000, nowTicks: T(60));      // +100k over 60s
          // 100_000 xp / (60 s / 3600) = 6_000_000 xp/hr
          Assert.InRange(t.XpPerHour, 5_999_000f, 6_001_000f);
      }

      [Fact]
      public void XpRing_ZeroCurrentXp_SkipsAppendAndPreservesPriorRate()
      {
          var t = new SessionTracker { XpWindowMinutes = 5 };
          Feed(t, currentXp: 1_000_000, nowTicks: T(0));
          Feed(t, currentXp: 1_100_000, nowTicks: T(60));
          float rateBefore = t.XpPerHour;
          Assert.True(rateBefore > 0f);
          // player component unresolved this frame — must NOT append or zero the rate
          Feed(t, currentXp: 0, nowTicks: T(120));
          Assert.Equal(rateBefore, t.XpPerHour);
      }

      [Fact]
      public void XpRing_TownFrame_ExcludeTownsTrue_DoesNotAppend()
      {
          var t = new SessionTracker { XpWindowMinutes = 5 };
          Feed(t, currentXp: 1_000_000, nowTicks: T(0));
          Feed(t, currentXp: 1_100_000, nowTicks: T(60));
          float before = t.XpPerHour;
          // Fabricated huge jump — if this appended, rate would spike wildly.
          Feed(t, currentXp: 9_999_999, nowTicks: T(120),
               excludeTowns: true, isTown: true, areaCode: "G1_town");
          Assert.Equal(before, t.XpPerHour);
      }

      [Fact]
      public void XpRing_SurvivesZoneCrossing_RateStaysConsistent()
      {
          var t = new SessionTracker { XpWindowMinutes = 5 };
          Feed(t, currentXp: 1_000_000, nowTicks: T(0),  areaHash: 1);
          Feed(t, currentXp: 1_100_000, nowTicks: T(60), areaHash: 1);
          float before = t.XpPerHour;
          // Zone change — hash differs — ring must NOT reset. Steady rate continues.
          Feed(t, currentXp: 1_200_000, nowTicks: T(120), areaHash: 2);
          Assert.InRange(t.XpPerHour, before - 100_000f, before + 100_000f);
      }

      [Fact]
      public void XpRing_ResetClearsRing_FirstPostResetTickReadsZeroRate()
      {
          var t = new SessionTracker { XpWindowMinutes = 5 };
          Feed(t, currentXp: 1_000_000, nowTicks: T(0));
          Feed(t, currentXp: 1_100_000, nowTicks: T(60));
          Assert.True(t.XpPerHour > 0f);
          t.Reset(T(120));
          // Baseline reseeds to whatever XP the first post-reset tick brings — delta=0.
          Feed(t, currentXp: 1_100_000, nowTicks: T(120));
          Assert.Equal(0f, t.XpPerHour);
      }

      [Fact]
      public void XpRing_FullWindow_ComputesRateFromOldestInWindow()
      {
          // 1-minute window -> slots = max(12, 12) = 12. Fill fully at 5s cadence.
          var t = new SessionTracker { XpWindowMinutes = 1 };
          long xp = 1_000_000;
          for (int i = 0; i < 12; i++)
          {
              Feed(t, currentXp: xp, nowTicks: T(i * 5));
              xp += 10_000; // +10k xp per 5s -> 7,200,000/hr steady rate
          }
          // 13th sample rotates the head; oldest becomes the T(5) sample.
          Feed(t, currentXp: xp, nowTicks: T(60));
          Assert.InRange(t.XpPerHour, 7_100_000f, 7_300_000f);
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  ```pwsh
  dotnet test C:/Users/minec/Documents/Projects/POE2GPS/tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SessionTrackerXpRingTests" --nologo
  ```

  Expected: build failure — `CS1501: No overload for method 'Update' takes 9 arguments` and `CS0117: 'SessionTracker' does not contain a definition for 'XpPerHour'` / `XpWindowMinutes`. That is the failing state — the 9-arg overload and the two new properties do not exist yet.

- [ ] **Step 3: Write minimal implementation**

  Edit `src/POE2Radar.Core/Session/SessionTracker.cs`. Add the XP ring block **inside the `SessionTracker` class**, immediately after the existing v2 fields (after `private int _xpEfficiency;` at L29):

  ```csharp
      // ---- XP ring buffer (Threshold — THR-XP-TRACKER) -----------------------------------------
      // Fixed-slot (nowTicks, cumulativeXp) samples for XP/hour session HUD chip. Zero per-tick
      // allocation once sized. Ring survives zone crossings — XP/hour is a grind metric, not a zone
      // metric. Town frames don't append (reuses ExcludeTownsFromPace — no new knob). Reset clears
      // the ring so the first post-reset XP-bearing Update re-seeds a delta=0 baseline.
      private long[]? _xpTicks;
      private long[]? _xpValues;
      private int     _xpSlots;              // 0 until first append sizes the ring
      private int     _xpCount;              // samples written (capped at _xpSlots)
      private int     _xpHead;               // next slot to write; also == oldest slot when full
      private int     _xpWindowMinutes = 5;
      private long    _sessionStartXp;       // baseline for fallback rate (SessionXpDelta/sessionHours)
      private bool    _sessionXpSeen;
      private float   _xpPerHour;            // last computed rate; preserved on zero-currentXp frames

      /// <summary>Last computed XP/hour rate. 0f until ring or session-fallback delta is meaningful.</summary>
      public float XpPerHour => _xpPerHour;

      /// <summary>
      /// Sliding-window size in minutes for the XP/hour rate. Setter clamps to [1,60] and on-change
      /// drops any accumulated ring state so the new window is populated fresh.
      /// </summary>
      public int XpWindowMinutes
      {
          get => _xpWindowMinutes;
          set
          {
              int v = value < 1 ? 1 : (value > 60 ? 60 : value);
              if (v == _xpWindowMinutes) return;
              _xpWindowMinutes = v;
              _xpTicks  = null;
              _xpValues = null;
              _xpSlots  = 0;
              _xpCount  = 0;
              _xpHead   = 0;
              // _sessionStartXp / _sessionXpSeen intentionally preserved: session baseline for the
              // fallback rate is a session concept, not a window concept.
          }
      }

      /// <summary>
      /// 9-arg overload of <see cref="Update(uint,string,int,int,float,long,bool,bool)"/> that also
      /// feeds a cumulative XP sample into the XP ring buffer. <paramref name="currentXp"/> of 0 (or
      /// negative) means the player-component read failed this frame — the append is skipped and the
      /// prior <see cref="XpPerHour"/> value is preserved (no NaN, no zero-spike). Town frames with
      /// <paramref name="excludeTowns"/>=true are also skipped, so the rate freezes on hideout entry
      /// then decays as older samples age out of the window.
      /// </summary>
      public SessionStats Update(
          uint   areaHash,
          string areaCode,
          int    areaLevel,
          int    playerLevel,
          float  hpPct,
          long   nowTicks,
          bool   excludeTowns,
          bool   isTown,
          long   currentXp)
      {
          var stats = Update(areaHash, areaCode, areaLevel, playerLevel, hpPct, nowTicks, excludeTowns, isTown);

          // Skip append on unresolved player component or town frames (with exclude enabled).
          if (currentXp <= 0)              return stats;
          if (excludeTowns && isTown)      return stats;

          // Lazy-allocate the ring on first real sample or after XpWindowMinutes changed.
          if (_xpTicks is null || _xpValues is null || _xpSlots == 0)
          {
              _xpSlots  = Math.Max(12, _xpWindowMinutes * 12); // ~5s cadence assumption
              _xpTicks  = new long[_xpSlots];
              _xpValues = new long[_xpSlots];
              _xpCount  = 0;
              _xpHead   = 0;
          }

          if (!_sessionXpSeen)
          {
              _sessionStartXp = currentXp;
              _sessionXpSeen  = true;
          }

          // Round-robin write. No allocation per tick.
          _xpTicks[_xpHead]  = nowTicks;
          _xpValues[_xpHead] = currentXp;
          _xpHead = (_xpHead + 1) % _xpSlots;
          if (_xpCount < _xpSlots) _xpCount++;

          if (_xpCount >= _xpSlots)
          {
              // Ring full: window-based rate. Oldest sample lives at _xpHead (next-write slot).
              int  oldest = _xpHead;
              long span   = nowTicks - _xpTicks[oldest];
              if (span > 0)
              {
                  double windowHours = span / (double)TimeSpan.TicksPerHour;
                  long   delta       = currentXp - _xpValues[oldest];
                  _xpPerHour = (float)(delta / windowHours);
              }
          }
          else
          {
              // Fallback: session-average rate while the ring is still filling.
              long sessionSpan = nowTicks - _sessionStartTicks;
              if (sessionSpan > 0)
              {
                  double sessionHours = sessionSpan / (double)TimeSpan.TicksPerHour;
                  long   delta        = currentXp - _sessionStartXp;
                  _xpPerHour = (float)(delta / sessionHours);
              }
          }

          return stats;
      }
  ```

  Extend the shipped `Reset(long nowTicks)` (L104–L117) so it also drops XP state. Insert the following lines immediately before the closing `}` of `Reset` (right after `_mapZonesEntered = 0;`):

  ```csharp
          // XP ring: clear + drop baseline so the next XP-bearing Update reseeds a delta=0 sample.
          _xpTicks         = null;
          _xpValues        = null;
          _xpSlots         = 0;
          _xpCount         = 0;
          _xpHead          = 0;
          _sessionStartXp  = 0;
          _sessionXpSeen   = false;
          _xpPerHour       = 0f;
  ```

- [ ] **Step 4: Run test to verify it passes**

  ```pwsh
  dotnet test C:/Users/minec/Documents/Projects/POE2GPS/tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SessionTrackerXpRingTests" --nologo
  ```

  Expected: `Passed! - Failed: 0, Passed: 8, Skipped: 0` (all 8 facts green — the two clamp facts + fill fallback + zero-skip + town-freeze + zone-cross + Reset re-seed + full-window steady rate).

  Then re-run the full existing SessionTracker suite to confirm the 8-arg overload / Reset behavior didn't regress:

  ```pwsh
  dotnet test C:/Users/minec/Documents/Projects/POE2GPS/tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SessionTrackerTests|FullyQualifiedName~SessionTrackerV2Tests" --nologo
  ```

  Expected: all previously-green facts remain green.

- [ ] **Step 5: Commit**

  ```pwsh
  git -C C:/Users/minec/Documents/Projects/POE2GPS add src/POE2Radar.Core/Session/SessionTracker.cs tests/POE2Radar.Tests/Session/SessionTrackerXpRingTests.cs
  git -C C:/Users/minec/Documents/Projects/POE2GPS commit -m @'
  task THR-XP-TRACKER: SessionTracker XP ring buffer + 9-arg Update overload

  Adds sliding-window XP/hour rate machinery inside the pure session tracker
  so the Threshold XP HUD chip has a stable data source without a signature
  break. Ring is (nowTicks, cumulativeXp) round-robin, slots = max(12, XpWindowMinutes*12)
  at ~5s cadence, zero per-tick allocation once sized. Rate is
  (latest - oldest_in_window) / windowHours once the ring is full; fallback is
  SessionXpDelta / sessionHours while it fills. Ring survives zone crossings
  (grind metric, not zone metric). Town frames don't append — reuses
  ExcludeTownsFromPace, no new knob. currentXp<=0 (player component unresolved)
  skips the append and preserves the prior rate so we never emit NaN or a
  zero-spike. Reset(nowTicks) now also clears the ring and drops the XP
  baseline so the first post-reset XP-bearing Update re-seeds delta=0.
  Ships as a 9-arg OVERLOAD delegating to the shipped 8-arg Update, so every
  existing SessionHud caller keeps compiling untouched.
  '@
  ```

  Expected: single commit landing `src/POE2Radar.Core/Session/SessionTracker.cs` + `tests/POE2Radar.Tests/Session/SessionTrackerXpRingTests.cs`. `git status` clean.

---

### Task 7: THR-XP-SETTINGS — SessionHud XP toggle + window minutes clamp

**Files:**
- Create: `tests/POE2Radar.Tests/Config/SessionHudXpSettingsTests.cs`
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs:616` (SessionHudSettings class — add `ShowXpRate`, `XpWindowMinutes` with setter-clamp)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs:1388` (GET /api/state mirror — after `sessionHudExcludeTowns`)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs:1500` (POST /api/settings switch — after `sessionHudExcludeTowns` case)
- Test: `tests/POE2Radar.Tests/Config/SessionHudXpSettingsTests.cs`

**Interfaces:**
- Consumes: (none — leaf config task, produces primitives)
- Produces:
  - `SessionHudSettings.ShowXpRate: bool` (default `false`) — consumed by THR-XP-TRACKER (gate on `_live.PlayerExperience` call in `RadarApp.WorldTick`) and THR-XP-HUD-RENDER (row visibility)
  - `SessionHudSettings.XpWindowMinutes: int` (default `5`, clamped 1..60 in setter) — consumed by THR-XP-TRACKER for ring-buffer sizing (`slots = max(12, XpWindowMinutes * 12)`)
  - `/api/state` GET fields: `sessionHudShowXpRate`, `sessionHudXpWindowMinutes`
  - `/api/settings` POST keys: `sessionHudShowXpRate` (bool), `sessionHudXpWindowMinutes` (int)

---

- [ ] **Step 1: Write the failing test — `tests/POE2Radar.Tests/Config/SessionHudXpSettingsTests.cs`**

```csharp
using System.Text.Json;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// THR-XP-SETTINGS: two new fields on SessionHudSettings drive the XP-rate row.
/// ShowXpRate defaults OFF (PMS-6 opt-in policy). XpWindowMinutes is clamped
/// 1..60 in the setter (5 min vs 30 min is a real preference split among grinders,
/// but zero/negative values would break the ring-buffer sizing in the tracker).
/// The two fields must round-trip through JsonSerializer so radar_settings.json
/// persists them across restarts, and must survive the /api/settings JSON POST
/// key mirror (sessionHudShowXpRate + sessionHudXpWindowMinutes).
/// </summary>
public class SessionHudXpSettingsTests
{
    [Fact]
    public void ShowXpRate_defaults_off()
    {
        var s = new SessionHudSettings();
        Assert.False(s.ShowXpRate);
    }

    [Fact]
    public void XpWindowMinutes_defaults_to_five()
    {
        var s = new SessionHudSettings();
        Assert.Equal(5, s.XpWindowMinutes);
    }

    [Fact]
    public void XpWindowMinutes_clamps_below_one_to_one()
    {
        var s = new SessionHudSettings { XpWindowMinutes = 0 };
        Assert.Equal(1, s.XpWindowMinutes);

        s.XpWindowMinutes = -17;
        Assert.Equal(1, s.XpWindowMinutes);
    }

    [Fact]
    public void XpWindowMinutes_clamps_above_sixty_to_sixty()
    {
        var s = new SessionHudSettings { XpWindowMinutes = 120 };
        Assert.Equal(60, s.XpWindowMinutes);

        s.XpWindowMinutes = int.MaxValue;
        Assert.Equal(60, s.XpWindowMinutes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    public void XpWindowMinutes_accepts_boundary_and_common_values(int v)
    {
        var s = new SessionHudSettings { XpWindowMinutes = v };
        Assert.Equal(v, s.XpWindowMinutes);
    }

    /// <summary>
    /// Simulates /api/settings POST → serialize → deserialize round-trip.
    /// The two new keys must survive radar_settings.json persistence so a grinder
    /// who toggles ShowXpRate on and picks a 30-minute window keeps that after
    /// restarting the overlay.
    /// </summary>
    [Fact]
    public void SessionHud_xp_fields_round_trip_through_json()
    {
        var root = new RadarSettings();
        root.SessionHud.ShowXpRate = true;
        root.SessionHud.XpWindowMinutes = 30;

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var json = JsonSerializer.Serialize(root, opts);
        var back = JsonSerializer.Deserialize<RadarSettings>(json, opts);

        Assert.NotNull(back);
        Assert.True(back!.SessionHud.ShowXpRate);
        Assert.Equal(30, back.SessionHud.XpWindowMinutes);
    }

    /// <summary>
    /// Clamp still fires when the on-disk file was edited by hand to an
    /// out-of-range value (or by an older build with no clamp).
    /// </summary>
    [Fact]
    public void Deserialized_XpWindowMinutes_out_of_range_is_clamped()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = "{\"sessionHud\":{\"xpWindowMinutes\":9999}}";
        var back = JsonSerializer.Deserialize<RadarSettings>(json, opts);
        Assert.NotNull(back);
        Assert.Equal(60, back!.SessionHud.XpWindowMinutes);
    }
}
```

- [ ] **Step 2: Run the test — expect FAIL (compile error: `SessionHudSettings` has no `ShowXpRate` / `XpWindowMinutes`)**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SessionHudXpSettingsTests" -v minimal
```

Expected output contains `error CS0117: 'SessionHudSettings' does not contain a definition for 'ShowXpRate'` (and same for `XpWindowMinutes`). Build fails; no tests run.

- [ ] **Step 3: Add fields with clamping setter — edit `src/POE2Radar.Overlay/Config/RadarSettings.cs`**

Replace the body of `SessionHudSettings` (currently lines 616–630) with:

```csharp
public sealed class SessionHudSettings
{
    public bool   Enabled               { get; set; } = false;
    public bool   ShowPace              { get; set; } = false;
    public bool   ShowZoneContext       { get; set; } = false;
    public bool   ShowDeaths            { get; set; } = false;
    public bool   ShowKills             { get; set; } = false;
    // THR-XP-SETTINGS: XP-rate row. Opt-in OFF per PMS-6 policy — gates the
    // _live.PlayerExperience(localPlayer) read in RadarApp.WorldTick, so with
    // this false there is literally zero XP-tracking work per tick.
    public bool   ShowXpRate            { get; set; } = false;
    // Rolling window over which XP/hr is averaged. 5-min is default; 30-min is
    // a real preference split among grinders. Clamped 1..60 in setter so the
    // downstream ring-buffer sizing (slots = max(12, minutes * 12)) can't go
    // zero/negative from a hand-edited radar_settings.json.
    private int _xpWindowMinutes = 5;
    public int    XpWindowMinutes
    {
        get => _xpWindowMinutes;
        set => _xpWindowMinutes = value < 1 ? 1 : (value > 60 ? 60 : value);
    }
    public string Anchor                { get; set; } = "TopLeft";
    // Legal values: "TopLeft", "TopRight", "BottomLeft", "BottomRight"
    // Mirrors NavMenuCorner (RadarSettings.cs line 55) — plain string, no C# enum.
    public int    OffsetX               { get; set; } = 0; // pixels inward from the anchored corner (positive = toward screen center)
    public int    OffsetY               { get; set; } = 0; // pixels inward from the anchored corner (positive = toward screen center)
    // Behavior-tuning flag (NOT a visibility toggle): defaults TRUE so towns are excluded from pace.
    // Reused by XP HUD for the freeze-in-town rule — no separate knob per spec §2.
    public bool   ExcludeTownsFromPace  { get; set; } = true;
}
```

- [ ] **Step 4: Mirror the two fields in `/api/state` GET — edit `src/POE2Radar.Overlay/Web/ApiServer.cs`**

Locate the anonymous object literal at line 1388–1396 (starts `sessionHudEnabled = _settings.SessionHud.Enabled,`) and insert the two new lines immediately after `sessionHudShowKills`:

```csharp
        sessionHudEnabled          = _settings.SessionHud.Enabled,
        sessionHudShowPace         = _settings.SessionHud.ShowPace,
        sessionHudShowZoneContext  = _settings.SessionHud.ShowZoneContext,
        sessionHudShowDeaths       = _settings.SessionHud.ShowDeaths,
        sessionHudShowKills        = _settings.SessionHud.ShowKills,
        sessionHudShowXpRate       = _settings.SessionHud.ShowXpRate,
        sessionHudXpWindowMinutes  = _settings.SessionHud.XpWindowMinutes,
        sessionHudAnchor           = _settings.SessionHud.Anchor,
        sessionHudOffsetX          = _settings.SessionHud.OffsetX,
        sessionHudOffsetY          = _settings.SessionHud.OffsetY,
        sessionHudExcludeTowns     = _settings.SessionHud.ExcludeTownsFromPace,
```

- [ ] **Step 5: Mirror the two fields in `/api/settings` POST switch — edit `src/POE2Radar.Overlay/Web/ApiServer.cs`**

Locate the switch cases at lines 1500–1508 (starting `case "sessionHudEnabled"`) and insert two new cases immediately after the `sessionHudShowKills` case, matching the existing per-row-toggle pattern:

```csharp
                case "sessionHudEnabled" when TryBool(p.Value, out var b): _settings.SessionHud.Enabled = b; applied.Add(p.Name); break;
                case "sessionHudShowPace" when TryBool(p.Value, out var b): _settings.SessionHud.ShowPace = b; applied.Add(p.Name); break;
                case "sessionHudShowZoneContext" when TryBool(p.Value, out var b): _settings.SessionHud.ShowZoneContext = b; applied.Add(p.Name); break;
                case "sessionHudShowDeaths" when TryBool(p.Value, out var b): _settings.SessionHud.ShowDeaths = b; applied.Add(p.Name); break;
                case "sessionHudShowKills" when TryBool(p.Value, out var b): _settings.SessionHud.ShowKills = b; applied.Add(p.Name); break;
                case "sessionHudShowXpRate" when TryBool(p.Value, out var b): _settings.SessionHud.ShowXpRate = b; applied.Add(p.Name); break;
                case "sessionHudXpWindowMinutes" when TryInt(p.Value, out var n): _settings.SessionHud.XpWindowMinutes = n; applied.Add(p.Name); break;
                case "sessionHudExcludeTowns" when TryBool(p.Value, out var b): _settings.SessionHud.ExcludeTownsFromPace = b; applied.Add(p.Name); break;
                case "sessionHudAnchor" when TryString(p.Value, out var s): _settings.SessionHud.Anchor = s.Trim(); applied.Add(p.Name); break;
                case "sessionHudOffsetX" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetX = n; applied.Add(p.Name); break;
                case "sessionHudOffsetY" when TryInt(p.Value, out var n): _settings.SessionHud.OffsetY = n; applied.Add(p.Name); break;
```

Note: because `XpWindowMinutes`'s setter clamps, hand-crafted POSTs with `sessionHudXpWindowMinutes: 9999` land as `60`, not rejected — matches the deserialize-clamp test in Step 1.

- [ ] **Step 6: Run tests + full solution build — expect PASS**

```powershell
dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~SessionHudXpSettingsTests" -v minimal
```

Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0` (7 `[Fact]` + 4 `[Theory]` inline data = 8 unique test names; the theory reports as 4 rows so 7 + 4 = 11 total, or Xunit collapses to 8 — either way `Failed: 0`).

Then confirm the full build still passes so the ApiServer edits compile:

```powershell
dotnet build POE2GPS.sln -v minimal
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```powershell
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs tests/POE2Radar.Tests/Config/SessionHudXpSettingsTests.cs
git commit -m @'
THR-XP-SETTINGS: SessionHud XP toggle + window clamp + api mirror

- SessionHudSettings gains ShowXpRate (opt-in default OFF per PMS-6)
  and XpWindowMinutes (default 5, setter-clamped 1..60 so downstream
  ring-buffer sizing can not go zero/negative from a hand-edited json).
- /api/state exposes sessionHudShowXpRate + sessionHudXpWindowMinutes;
  /api/settings accepts them via the existing per-row-toggle pattern.
- Reset piggybacks Ctrl+Alt+R and town-freeze reuses
  ExcludeTownsFromPace, so no new hotkey and no new knob.
- Tests cover default OFF, default 5, boundary clamp both sides,
  common values pass through, JSON round-trip, and deserialize-clamp.
'@
```

---

### Task 8: THR-XP-RENDER — XP/hour Session HUD row + zero-cost-when-off gate

Consumes Task 5 (`PoE2XpCurveLoader`), Task 6 (`SessionTracker.Update` 9-arg overload + `SessionStats` XP fields), Task 7 (`SessionHudSettings.ShowXpRate` / `XpWindowMinutes`). Ships (a) the pure formatter + humanization, (b) the `DrawSessionHud` row + cache-key extension, (c) the `RadarApp.WorldTick` gated `_live.PlayerExperience(localPlayer)` read that piggybacks on the existing `_levelRefreshTick` ~5 s cadence, (d) render tests + zero-cost-when-off spy.

**Files:**
- Create: `src/POE2Radar.Overlay/Overlay/SessionHudXpFormatter.cs`
- Create: `tests/POE2Radar.Tests/SessionHudXpRenderTests.cs`
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` — cache fields at L74-83, `DrawSessionHud` cache-key comparands at L885-903, cache assignment block L905-922, row-build block L941-946 (append after `ShowKills`)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` — new `_currentXp` field near L226, gated read inside `WorldTick` `_levelRefreshTick % 150 == 0` block at L1751, extended `_session.Update` 9-arg call at L1542-1550

**Interfaces:**

- Consumes:
  - `PoE2XpCurveLoader.XpToNextLevel(int level, long currentXp) → long?` — from `THR-XP-CURVE-LOADER`
  - `PoE2XpCurveLoader.TimeToNextLevel(int level, long currentXp, float xpPerHour) → TimeSpan?` — from `THR-XP-CURVE-LOADER`
  - `SessionTracker.Update(uint, string, int, int, float, long, bool, bool, long currentXp) → SessionStats` — from `THR-XP-TRACKER`
  - `SessionStats.XpPerHour: float`, `SessionStats.CurrentXp: long`, `SessionStats.SessionXpDelta: long`, `SessionStats.RingFilling: bool` — from `THR-XP-TRACKER`
  - `SessionHudSettings.ShowXpRate: bool = false`, `SessionHudSettings.XpWindowMinutes: int = 5` — from `THR-XP-SETTINGS`
  - `Poe2Live.PlayerExperience(nint) → long` — shipped by Campaign Probe

- Produces:
  - `POE2Radar.Overlay.Overlay.SessionHudXpFormatter.Humanize(long value) → string`
  - `POE2Radar.Overlay.Overlay.SessionHudXpFormatter.FormatXpRow(float xpPerHour, int currentLevel, long currentXp, bool ringFilling, Func<int,long,float,TimeSpan?> timeToNextResolver) → (string primary, string? secondary, bool noData)`
  - `POE2Radar.Overlay.Config.SessionHudSettingsExt.ShouldReadXpRate(this SessionHudSettings hud) → bool`

---

- [ ] **Step 1: Write the failing tests (formatter + spy gate).**

  Create `tests/POE2Radar.Tests/SessionHudXpRenderTests.cs`:

  ```csharp
  using POE2Radar.Overlay.Overlay;
  using POE2Radar.Overlay.Config;

  public class SessionHudXpRenderTests
  {
      // ── Humanization thresholds (spec §4.4): <1K raw, <10K "1.24K", <1M "245K", <1B "1.24M", else "2.1B". ──

      [Theory]
      [InlineData(0L,          "0")]
      [InlineData(1L,          "1")]
      [InlineData(999L,        "999")]
      [InlineData(1_000L,      "1.00K")]
      [InlineData(1_240L,      "1.24K")]
      [InlineData(9_999L,      "9.99K")]
      [InlineData(10_000L,     "10K")]
      [InlineData(245_000L,    "245K")]
      [InlineData(999_999L,    "999K")]
      [InlineData(1_000_000L,  "1.00M")]
      [InlineData(1_240_000L,  "1.24M")]
      [InlineData(999_999_999L,"999M")]
      [InlineData(1_000_000_000L, "1.00B")]
      [InlineData(2_100_000_000L, "2.10B")]
      public void Humanize_MatchesThresholdTable(long value, string expected)
      {
          Assert.Equal(expected, SessionHudXpFormatter.Humanize(value));
      }

      [Fact]
      public void Humanize_NegativeIsClampedToZero()
      {
          Assert.Equal("0", SessionHudXpFormatter.Humanize(-42));
      }

      // ── FormatXpRow — single line when both fit, split while ring is still filling. ──

      [Fact]
      public void FormatXpRow_SingleLine_WhenRingFilled_AndTtlPresent()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    1_240_000f,
              currentLevel: 85,
              currentXp:    1_000_000L,
              ringFilling:  false,
              timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(12));

          Assert.Equal("XP/hr    1.24M  (12m to L86)", r.primary);
          Assert.Null(r.secondary);
          Assert.False(r.noData);
      }

      [Fact]
      public void FormatXpRow_SplitsTwoLines_WhileRingFilling()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    245_000f,
              currentLevel: 84,
              currentXp:    500_000L,
              ringFilling:  true,
              timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(45));

          Assert.Equal("XP/hr    245K", r.primary);
          Assert.Equal("(45m to L85)", r.secondary);
          Assert.False(r.noData);
      }

      [Fact]
      public void FormatXpRow_SuppressesTtl_WhenResolverReturnsNull()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    100f,
              currentLevel: 100,   // no next level
              currentXp:    4_000_000_000L,
              ringFilling:  false,
              timeToNextResolver: (_, _, _) => null);

          Assert.Equal("XP/hr    100", r.primary);
          Assert.Null(r.secondary);
          Assert.False(r.noData);
      }

      [Fact]
      public void FormatXpRow_NoData_WhenXpPerHourZero()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    0f,
              currentLevel: 85,
              currentXp:    1_000_000L,
              ringFilling:  false,
              timeToNextResolver: (_, _, _) => null);

          Assert.Equal("XP/hr    --", r.primary);
          Assert.Null(r.secondary);
          Assert.True(r.noData);   // yellow-tint tell — matches deaths no-data pattern
      }

      [Fact]
      public void FormatXpRow_TtlHours_FormatsAsHoursAndMinutes()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    50_000f,
              currentLevel: 90,
              currentXp:    2_000_000L,
              ringFilling:  false,
              timeToNextResolver: (_, _, _) => TimeSpan.FromMinutes(135));  // 2h 15m

          Assert.Equal("XP/hr    50.0K  (2h 15m to L91)", r.primary);
      }

      [Fact]
      public void FormatXpRow_TtlDays_FormatsAsDaysAndHours()
      {
          var r = SessionHudXpFormatter.FormatXpRow(
              xpPerHour:    1_000f,
              currentLevel: 95,
              currentXp:    2_000_000_000L,
              ringFilling:  false,
              timeToNextResolver: (_, _, _) => TimeSpan.FromHours(50));   // 2d 2h

          Assert.Equal("XP/hr    1.00K  (2d 2h to L96)", r.primary);
      }

      // ── Zero-cost-when-off gate: RadarApp.WorldTick MUST skip the _live.PlayerExperience
      //    accessor when hud.Enabled == false OR hud.ShowXpRate == false. Assert (a) the
      //    extension gate returns false, (b) the caller spy never fires, (c) zero managed
      //    allocations across 1000 disabled ticks. ──

      [Fact]
      public void ShouldReadXpRate_FalseWhenHudDisabled()
      {
          var hud = new SessionHudSettings { Enabled = false, ShowXpRate = true };
          Assert.False(hud.ShouldReadXpRate());
      }

      [Fact]
      public void ShouldReadXpRate_FalseWhenShowXpRateOff()
      {
          var hud = new SessionHudSettings { Enabled = true, ShowXpRate = false };
          Assert.False(hud.ShouldReadXpRate());
      }

      [Fact]
      public void ShouldReadXpRate_TrueOnlyWhenBothOn()
      {
          var hud = new SessionHudSettings { Enabled = true, ShowXpRate = true };
          Assert.True(hud.ShouldReadXpRate());
      }

      [Fact]
      public void ZeroCostWhenOff_1000Ticks_NoAllocations_NoAccessorCalls()
      {
          var hud = new SessionHudSettings { Enabled = false, ShowXpRate = false };
          int spyCalls = 0;

          // Warm up JIT + settle any static-init allocation off the measured window.
          for (int i = 0; i < 16; i++) { if (hud.ShouldReadXpRate()) spyCalls++; }
          GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

          long before = GC.GetAllocatedBytesForCurrentThread();
          for (int i = 0; i < 1000; i++)
          {
              // Mirror the RadarApp.WorldTick call site pattern EXACTLY: gate first,
              // never invoke the accessor (spy stands in for _live.PlayerExperience).
              if (hud.ShouldReadXpRate()) { spyCalls++; }
          }
          long after = GC.GetAllocatedBytesForCurrentThread();

          Assert.Equal(0, spyCalls);
          Assert.Equal(0, after - before);
      }
  }
  ```

- [ ] **Step 2: Run tests to confirm they fail (types don't exist yet).**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~SessionHudXpRenderTests
  ```

  Expected: `CS0246: The type or namespace name 'SessionHudXpFormatter' could not be found` and `CS1061: 'SessionHudSettings' does not contain a definition for 'ShouldReadXpRate'`. Build failure = red bar as required by TDD.

- [ ] **Step 3: Create the pure formatter + settings-gate extension.**

  Write `src/POE2Radar.Overlay/Overlay/SessionHudXpFormatter.cs`:

  ```csharp
  using System;
  using System.Globalization;
  using POE2Radar.Overlay.Config;

  namespace POE2Radar.Overlay.Overlay;

  /// <summary>
  /// Pure static formatter for the XP/hour Session HUD row (Threshold spec §4.4).
  ///
  /// Kept dependency-free so the test project can cover the humanization thresholds
  /// and the split-vs-single-line branch without pulling Direct2D. All rendering
  /// (brush, DrawText, panel geometry) stays in <c>OverlayRenderer.DrawSessionHud</c>.
  /// </summary>
  public static class SessionHudXpFormatter
  {
      // ── Humanization thresholds locked by spec §4.4:
      //    <1K   → raw digits
      //    <10K  → "1.24K" (two decimals)
      //    <1M   → "245K"  (integer)
      //    <1B   → "1.24M" (two decimals)
      //    else  → "2.10B" (two decimals) ──
      public static string Humanize(long value)
      {
          if (value < 0) value = 0;
          if (value < 1_000L)             return value.ToString(CultureInfo.InvariantCulture);
          if (value < 10_000L)            return (value / 1_000d).ToString("0.00", CultureInfo.InvariantCulture) + "K";
          if (value < 1_000_000L)         return (value / 1_000L).ToString(CultureInfo.InvariantCulture) + "K";
          if (value < 1_000_000_000L)     return (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M";
          return (value / 1_000_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "B";
      }

      /// <summary>
      /// Build the XP row strings. Single line when the ring has filled and there is
      /// something to say on both halves; two lines while the ring is still filling
      /// (so the panel stays honest during the first window worth of samples).
      ///
      /// Returns <c>noData=true</c> when <paramref name="xpPerHour"/> is zero — the
      /// caller paints the row in the shared yellow "no data" tint used by the
      /// deaths row.
      /// </summary>
      public static (string primary, string? secondary, bool noData) FormatXpRow(
          float xpPerHour,
          int   currentLevel,
          long  currentXp,
          bool  ringFilling,
          Func<int, long, float, TimeSpan?> timeToNextResolver)
      {
          if (xpPerHour <= 0f)
              return ("XP/hr    --", null, true);

          var rateText = Humanize((long)xpPerHour);
          var ttl      = timeToNextResolver(currentLevel, currentXp, xpPerHour);
          var ttlText  = ttl.HasValue ? FormatTimeToNext(ttl.Value, currentLevel + 1) : null;

          if (ttlText == null)
              return ($"XP/hr    {rateText}", null, false);

          if (ringFilling)
              return ($"XP/hr    {rateText}", ttlText, false);

          return ($"XP/hr    {rateText}  {ttlText}", null, false);
      }

      private static string FormatTimeToNext(TimeSpan span, int nextLevel)
      {
          // Clamp negative / absurd spans to zero minutes so the row never renders a
          // "-3m to L86" oddity if the resolver returns a stale clock delta.
          if (span < TimeSpan.Zero) span = TimeSpan.Zero;

          if (span.TotalMinutes < 60)
              return $"({(int)span.TotalMinutes}m to L{nextLevel})";

          if (span.TotalHours < 24)
              return $"({(int)span.TotalHours}h {span.Minutes}m to L{nextLevel})";

          return $"({(int)span.TotalDays}d {span.Hours}h to L{nextLevel})";
      }
  }

  /// <summary>
  /// Gate helper for the zero-cost-when-off contract (spec §2): the fallback
  /// <c>_live.PlayerExperience(localPlayer)</c> read at ~5 s cadence in
  /// <c>RadarApp.WorldTick</c> is NOT invoked unless both flags are on.
  /// Extracted as a static extension so the spy test can exercise the exact
  /// same predicate the production call site uses — zero allocations, zero
  /// closures.
  /// </summary>
  public static class SessionHudSettingsExt
  {
      public static bool ShouldReadXpRate(this SessionHudSettings hud)
          => hud.Enabled && hud.ShowXpRate;
  }
  ```

- [ ] **Step 4: Run tests — formatter + gate green.**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~SessionHudXpRenderTests
  ```

  Expected: `Passed! - Failed: 0, Passed: 20+, Skipped: 0`. All humanization rows, both format-row branches, all three gate assertions, and the 1000-tick zero-alloc spy pass.

- [ ] **Step 5: Extend `OverlayRenderer` cache fields for the XP row.**

  In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`, extend the D1 cache-field block declared at L74-83:

  ```csharp
  // ── D1: Session-HUD line cache (render-thread-owned). Rebuilt only when any input changes. ──
  private (string text, bool isDeath)[]? _hudLines;
  private int _hudCacheSessionSec, _hudCacheZoneSec, _hudCacheZones, _hudCacheAreaLevel, _hudCacheDeaths, _hudCacheDeathsHere;
  private float _hudCacheZonesPerHr;
  private string? _hudCacheZoneName;
  private bool _hudCacheShowPace, _hudCacheShowZone, _hudCacheShowDeaths;
  private int _hudCacheKillsN, _hudCacheKillsM, _hudCacheKillsR, _hudCacheKillsU;
  private float _hudCacheMapsHr;
  private int _hudCacheXpEff;
  private bool _hudCacheShowKills;
  // THR-XP-RENDER: XP/hour row cache-key comparands.
  private float _hudCacheXpPerHour;
  private long  _hudCacheCurrentXp, _hudCacheSessionXpDelta;
  private bool  _hudCacheShowXpRate;
  ```

  Rename the pair `(string text, bool isDeath)` to keep the "no-data yellow tint" semantics — the existing `isDeath` flag already drives yellow rendering at L980-983, so the XP row simply sets `isDeath=true` on the no-data branch. No brush change needed.

- [ ] **Step 6: Extend `DrawSessionHud` cache-key comparand set + row build.**

  In `DrawSessionHud` (~L885-947), add the four new comparands to the change-detect chain and add the row-build block AFTER the existing `ShowKills` block:

  Comparand extension (append inside the `if (_hudLines == null || … )` chain at L903 boundary):

  ```csharp
              || sess.XpEfficiency   != _hudCacheXpEff
              || hud.ShowKills       != _hudCacheShowKills
              // THR-XP-RENDER: rebuild when XP rate / cumulative / delta / toggle move.
              || sess.XpPerHour      != _hudCacheXpPerHour
              || sess.CurrentXp      != _hudCacheCurrentXp
              || sess.SessionXpDelta != _hudCacheSessionXpDelta
              || hud.ShowXpRate      != _hudCacheShowXpRate)
  ```

  Assignment block (extend the `_hudCache…` writes at L922 boundary):

  ```csharp
              _hudCacheShowKills    = hud.ShowKills;
              _hudCacheXpPerHour    = sess.XpPerHour;
              _hudCacheCurrentXp    = sess.CurrentXp;
              _hudCacheSessionXpDelta = sess.SessionXpDelta;
              _hudCacheShowXpRate   = hud.ShowXpRate;
  ```

  Row-build block, appended AFTER the `if (hud.ShowKills) { … }` block (at L946 boundary, before `_hudLines = lines.ToArray();`):

  ```csharp
              if (hud.ShowXpRate)
              {
                  // Passing the static method group (not a lambda) keeps the call site
                  // allocation-free. Resolver returns null when level==100 or rate<=0.
                  var xp = SessionHudXpFormatter.FormatXpRow(
                      xpPerHour:    sess.XpPerHour,
                      currentLevel: sess.CurrentAreaLevel > 0 ? sess.CurrentAreaLevel : 1,
                      currentXp:    sess.CurrentXp,
                      ringFilling:  sess.RingFilling,
                      timeToNextResolver: POE2Radar.Core.Session.PoE2XpCurveLoader.TimeToNextLevel);

                  lines.Add((xp.primary, xp.noData));
                  if (xp.secondary != null)
                      lines.Add((xp.secondary, xp.noData));
              }
  ```

  Note: `sess.CurrentAreaLevel` is the ZONE level, not the character level. If Task 6 exposes `sess.CurrentPlayerLevel` on `SessionStats`, prefer that; otherwise the renderer uses the character level cached in `RadarApp._charLevel` via the tracker snapshot's `CurrentAreaLevel` field extension. Confirm the field name with the Task 6 producer before merging — if the tracker exposes `CharacterLevel`, swap `sess.CurrentAreaLevel` → `sess.CharacterLevel` on the `currentLevel:` line.

- [ ] **Step 7: Gate the XP read in `RadarApp.WorldTick` + wire the 9-arg `Update` overload.**

  In `src/POE2Radar.Overlay/RadarApp.cs`, add a private field near L226 alongside `_levelRefreshTick`:

  ```csharp
  private int _levelRefreshTick;                          // SR-6: slow-refresh PlayerLevel counter (world thread)
  private long _currentXp;                                // THR-XP-RENDER: gated slow-refresh XP (world thread, 0 = never read)
  ```

  Extend the `_levelRefreshTick % 150 == 0` block at L1751 so the XP accessor rides the SAME 5 s cadence — no new memory-read tick:

  ```csharp
  if (_levelRefreshTick++ % 150 == 0)
  {
      _charLevel = _live.PlayerLevel(localPlayer);        // SR-6: ~5 s cadence
      // THR-XP-RENDER: zero-cost-when-off. Gate reuses SessionHudSettingsExt.ShouldReadXpRate
      // so the exact same predicate is covered by the spy test. When either master gate or
      // row toggle is off, the accessor is NOT invoked and _currentXp stays at its prior value
      // (SessionTracker treats 0 as "skip append, re-emit prior rate" — spec §4.4).
      if (_settings.SessionHud.ShouldReadXpRate())
          _currentXp = _live.PlayerExperience(localPlayer);
  }
  ```

  Extend the `_session.Update(...)` call at L1542-1550 to the 9-arg overload — leave the 8-arg signature UNTOUCHED (spec §2: no breaking signature change):

  ```csharp
  _sessionSnapshot = _session.Update(
      snap.AreaHash,
      snap.AreaCode,
      snap.AreaLevel,
      snap.CharLevel,
      _hpPct,
      DateTime.UtcNow.Ticks,
      _settings.SessionHud.ExcludeTownsFromPace,
      isTown,
      _currentXp);   // THR-XP-RENDER: 9-arg overload from Task 6. 0 = "skip append, re-emit prior rate".
  ```

  Add the extension namespace at the top of `RadarApp.cs` if not already present:

  ```csharp
  using POE2Radar.Overlay.Overlay;   // SessionHudSettingsExt.ShouldReadXpRate
  ```

- [ ] **Step 8: Build the whole solution to confirm the wiring compiles + no unrelated regressions.**

  ```powershell
  dotnet build POE2Radar.sln -c Debug
  ```

  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. Any `CS…` from `SessionStats` missing `XpPerHour` / `CurrentXp` / `SessionXpDelta` / `RingFilling` means Task 6 hasn't landed — halt and rebase this task on top of THR-XP-TRACKER.

- [ ] **Step 9: Grep gate — no non-negotiable leaks in the diff.**

  ```powershell
  git diff --name-only | ForEach-Object { git diff -- $_ } | Select-String -Pattern 'WriteProcessMemory|Marshal\.Write|SendInput|keybd_event|mouse_event|VirtualProtect|Sikaka|GameHelper|superpowers/|TODO|FIXME|HACK|XXX'
  ```

  Expected: no matches. If any fires, fix at the source (rename comment, drop the leak) — spec §2 non-negotiables.

- [ ] **Step 10: Run the render tests one more time + commit.**

  ```powershell
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter FullyQualifiedName~SessionHudXp
  ```

  Expected: green.

  ```powershell
  git add src/POE2Radar.Overlay/Overlay/SessionHudXpFormatter.cs `
          src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs `
          src/POE2Radar.Overlay/RadarApp.cs `
          tests/POE2Radar.Tests/SessionHudXpRenderTests.cs
  git commit -m @'
  THR-XP-RENDER: XP/hour Session HUD row + zero-cost-when-off gate

  New pure formatter (Humanize + FormatXpRow) covers the spec §4.4 humanization
  thresholds and the split-vs-single-line branch. DrawSessionHud gains four
  cache-key comparands (XpPerHour, CurrentXp, SessionXpDelta, ShowXpRate) and a
  row-build block appended after the ShowKills cluster; no-data yellow tint
  reuses the existing deaths brush pattern.

  RadarApp.WorldTick gates _live.PlayerExperience behind
  SessionHudSettings.ShouldReadXpRate() inside the existing _levelRefreshTick
  ~5 s window — no new memory-read cadence, zero cost when the row is off.
  SessionTracker.Update call site swaps to the 9-arg overload; the 8-arg
  signature is left untouched per spec §2.

  Tests cover humanization thresholds, single/split branches, TTL suppression,
  hour/day TTL formatting, the three gate combinations, and a 1000-tick
  zero-allocation spy that mirrors the WorldTick call site pattern exactly.
  '@
  ```

  Follow-up (Task 9) will layer the tracker-side ring-buffer semantics, Reset re-seed, curve-loader coverage, dashboard mirror, PMS-6 close-out, and CHANGELOG themed body. This task only carries the render + gate slice.

---

### Task 9: XP HUD test consolidation + public-surface docs (README + CHANGELOG + PMS-6 closer)

**Files:**
- Create: `scripts/threshold-public-surface-gate.ps1`
- Create: `tests/POE2Radar.Tests/ThresholdPublicSurfaceGateTests.cs`
- Create: `tests/POE2Radar.Tests/Session/XpHudFullSuiteSmokeTests.cs`
- Modify: `README.md:119` (Session HUD bullet — add XP/hour clause)
- Modify: `CHANGELOG.md:6` (insert new `## [Unreleased] — v0.22 "Threshold"` block ABOVE the existing v0.21 Guided Campaign block; both stay Unreleased in parallel per spec §1)
- Modify: `docs/pending-manual-steps.md:21,33` (remove PMS-6 row from Active table; append 2026-07-09 entry to Done)
- Test: `tests/POE2Radar.Tests/ThresholdPublicSurfaceGateTests.cs`
- Test: `tests/POE2Radar.Tests/Session/XpHudFullSuiteSmokeTests.cs`

**Interfaces:**
- Consumes:
  - `PoE2XpCurveLoader.XpToNextLevel(int level, long currentXp) → long?` — from `THR-XP-CURVE-LOADER`
  - `PoE2XpCurveLoader.TimeToNextLevel(int level, long currentXp, float xpPerHour) → TimeSpan?` — from `THR-XP-CURVE-LOADER`
  - `SessionTracker.Update(long nowTicks, string areaCode, int areaLevel, int kills, int deaths, int mapsFinished, bool isTown, bool paceExcludesTowns, long currentXp)` — 9-arg overload from `THR-XP-TRACKER`
  - `SessionTracker.XpPerHour { get; }` — from `THR-XP-TRACKER`
  - `SessionTracker.Reset(long nowTicks)` — clears + re-seeds ring, from `THR-XP-TRACKER`
  - `SessionHudSettings.ShowXpRate: bool = false`, `XpWindowMinutes: int = 5` (clamped 1..60) — from `THR-XP-SETTINGS`
  - `OverlayRenderer.DrawSessionHud` new "XP/hr" row (yellow tint on rate==0) — from `THR-XP-RENDER`
  - `RadarApp.WorldTick` gated `_live.PlayerExperience(localPlayer)` fallback — from `THR-XP-RENDER`
  - `DisplayRules.BuiltInTileRules()` WaygateDevice static rule — from `THR-WAYGATE-RULE`
  - `OverlayRenderer.DrawMonolithPanel` caret + `mono-collapse` hit-rect + persisted `PanelCollapsed` bool — from `THR-MONO-COLLAPSE`
  - `OverlayRenderer.DrawAtlasContentIcons` corrected `Rect(ix, iy, ix + iconH, iy + iconH)` — from `THR-DRAW-FIX`
- Produces:
  - `scripts/threshold-public-surface-gate.ps1` — pre-tag PowerShell gate scanning README + CHANGELOG [Unreleased] Threshold block + new C# string literals for forbidden tokens (`Sikaka`, `GameHelper`, `superpowers`, `subagent`, `docs/superpowers/`, `Claude`, `SDD`) outside a line-number sentinel allowlist for pre-existing legitimate upstream attribution.
  - `ThresholdPublicSurfaceGateTests` — xunit fact that shells the gate script and asserts exit code 0.
  - `XpHudFullSuiteSmokeTests` — reflection smoke over every produced type of Tasks 1-8 so a silent revert of any upstream task trips a failing test here.
  - README `⏱️ Session HUD` bullet updated with `XP/hour` clause, `opt-in default OFF`, `1-60 min window`, `respects Exclude Towns From Pace`.
  - CHANGELOG `## [Unreleased] — v0.22 "Threshold"` block above v0.21 Guided Campaign block.
  - PMS-6 row removed from Active table; Done section gains 2026-07-09 entry closing Long List #34.

---

- [ ] **Step 1: Baseline — run the full test suite BEFORE any Task 9 edits so Tasks 1-8 pass on the branch head.**
  ```
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --nologo -v minimal
  ```
  Expected: `Passed!` with zero failures. If anything under `Session*`, `DisplayRules*`, `Overlay*`, `SettingsMigrator*`, or `Monolith*` is red, STOP and route the failing test back to its owning task (THR-XP-TRACKER / THR-WAYGATE-RULE / THR-XP-RENDER / THR-WAYGATE-TESTS / THR-MONO-COLLAPSE) — Task 9 must not paper over a Task 1-8 defect.

- [ ] **Step 2: Write the failing full-suite reflection smoke test — asserts every Task 1-8 produced type is present and its public shape matches the spec.**
  ```csharp
  // tests/POE2Radar.Tests/Session/XpHudFullSuiteSmokeTests.cs
  using System;
  using System.Linq;
  using System.Reflection;
  using Xunit;

  namespace POE2Radar.Tests.Session;

  /// <summary>
  /// Threshold Task 9 consolidation smoke. If any Task 1-8 produced type is silently
  /// renamed / deleted / signature-changed, this fires BEFORE the pre-tag grep gate so
  /// the release blocker points at the true owning task, not at README.
  /// </summary>
  public sealed class XpHudFullSuiteSmokeTests
  {
      private static readonly Assembly Core =
          typeof(POE2Radar.Core.Session.SessionTracker).Assembly;
      private static readonly Assembly Overlay =
          typeof(POE2Radar.Overlay.RadarApp).Assembly;

      [Fact]
      public void XpCurveLoader_Ships_XpToNextLevel_And_TimeToNextLevel()
      {
          var t = Core.GetType("POE2Radar.Core.Session.PoE2XpCurveLoader", throwOnError: true)!;
          var xpTo = t.GetMethod("XpToNextLevel", BindingFlags.Public | BindingFlags.Static);
          var ttl  = t.GetMethod("TimeToNextLevel", BindingFlags.Public | BindingFlags.Static);
          Assert.NotNull(xpTo);
          Assert.NotNull(ttl);
          Assert.Equal(typeof(long?), xpTo!.ReturnType);
          Assert.Equal(typeof(TimeSpan?), ttl!.ReturnType);
      }

      [Fact]
      public void SessionTracker_Ships_9Arg_Update_Overload_And_XpPerHour_And_Reset()
      {
          var t = typeof(POE2Radar.Core.Session.SessionTracker);
          var update9 = t.GetMethods()
              .Where(m => m.Name == "Update" && m.GetParameters().Length == 9)
              .ToList();
          Assert.Single(update9);
          var p = update9[0].GetParameters();
          Assert.Equal(typeof(long),   p[8].ParameterType); // currentXp
          var xph = t.GetProperty("XpPerHour");
          Assert.NotNull(xph);
          Assert.Equal(typeof(float), xph!.PropertyType);
          var reset = t.GetMethod("Reset", new[] { typeof(long) });
          Assert.NotNull(reset);
      }

      [Fact]
      public void SessionHudSettings_Ships_ShowXpRate_False_And_XpWindowMinutes_5()
      {
          var t = Overlay.GetType(
              "POE2Radar.Overlay.Config.SessionHudSettings", throwOnError: true)!;
          var inst = Activator.CreateInstance(t)!;
          var show = (bool)t.GetProperty("ShowXpRate")!.GetValue(inst)!;
          var win  = (int) t.GetProperty("XpWindowMinutes")!.GetValue(inst)!;
          Assert.False(show); // opt-in default OFF
          Assert.Equal(5, win);
      }

      [Fact]
      public void SessionHudSettings_XpWindowMinutes_ClampsTo_1_Through_60()
      {
          var t = Overlay.GetType(
              "POE2Radar.Overlay.Config.SessionHudSettings", throwOnError: true)!;
          var inst = Activator.CreateInstance(t)!;
          var prop = t.GetProperty("XpWindowMinutes")!;
          prop.SetValue(inst, 0);
          Assert.Equal(1, (int)prop.GetValue(inst)!);
          prop.SetValue(inst, 999);
          Assert.Equal(60, (int)prop.GetValue(inst)!);
      }

      [Fact]
      public void DisplayRules_BuiltInTileRules_Includes_WaygateDevice()
      {
          var t = Overlay.GetType(
              "POE2Radar.Overlay.Web.DisplayRules", throwOnError: true)!;
          var m = t.GetMethod("BuiltInTileRules", BindingFlags.Public | BindingFlags.Static);
          Assert.NotNull(m);
          var rules = (System.Collections.IEnumerable)m!.Invoke(null, null)!;
          var hit = false;
          foreach (var r in rules)
          {
              var name = r.GetType().GetProperty("Name")?.GetValue(r) as string
                       ?? r.GetType().GetProperty("EntityName")?.GetValue(r) as string;
              if (name == "WaygateDevice") { hit = true; break; }
          }
          Assert.True(hit, "BuiltInTileRules must seed a WaygateDevice rule (THR-WAYGATE-RULE).");
      }

      [Fact]
      public void MonolithSettings_Ships_PanelCollapsed_Bool()
      {
          var t = Overlay.GetType(
              "POE2Radar.Overlay.MonolithSettings", throwOnError: false)
              ?? Overlay.GetType(
              "POE2Radar.Overlay.Overlay.MonolithSettings", throwOnError: true)!;
          var prop = t.GetProperty("PanelCollapsed");
          Assert.NotNull(prop);
          Assert.Equal(typeof(bool), prop!.PropertyType);
      }

      [Fact]
      public void SettingsMigrator_Ships_BuiltInTileRulesSeeded_Legacy_Map_Entry()
      {
          var t = Overlay.GetType(
              "POE2Radar.Overlay.Config.SettingsMigrator", throwOnError: true)!;
          var mapField = t.GetField("Map",
              BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
          Assert.NotNull(mapField);
          var map = mapField!.GetValue(null) as System.Collections.IDictionary;
          Assert.NotNull(map);
          Assert.True(map!.Contains("BuiltInTileRulesSeeded"),
              "SettingsMigrator.Map must carry a defensive BuiltInTileRulesSeeded entry (spec §2).");
      }
  }
  ```

- [ ] **Step 3: Run the reflection smoke test to verify it passes on top of Tasks 1-8.**
  ```
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~XpHudFullSuiteSmokeTests" --nologo -v minimal
  ```
  Expected: `Passed: 7`. If any assertion fails, it names the specific Task (THR-XP-CURVE-LOADER / THR-XP-TRACKER / THR-XP-SETTINGS / THR-WAYGATE-RULE / THR-MONO-COLLAPSE) whose ship contract regressed — fix at the source, not here.

- [ ] **Step 4: Write the failing public-surface gate xunit test — it will shell a script we have not written yet, so the process launch fails.**
  ```csharp
  // tests/POE2Radar.Tests/ThresholdPublicSurfaceGateTests.cs
  using System;
  using System.Diagnostics;
  using System.IO;
  using Xunit;

  namespace POE2Radar.Tests;

  /// <summary>
  /// Threshold pre-tag public-surface gate. Fails the build if any release-surface file
  /// (README.md, CHANGELOG [Unreleased] Threshold block, new src/**/*.cs string literals)
  /// leaks internal-tooling tokens per feedback_no_internal_tooling_in_public_surfaces
  /// memory. Scoped by line-number sentinel so pre-existing legitimate upstream attribution
  /// (README §Testing / §Docs / §Attribution, scripts/compliance-gate.ps1) is allowlisted.
  /// </summary>
  public sealed class ThresholdPublicSurfaceGateTests
  {
      [Fact]
      public void PublicSurfaceGate_Passes_On_Current_Tree()
      {
          var repoRoot = FindRepoRoot();
          var script = Path.Combine(repoRoot, "scripts", "threshold-public-surface-gate.ps1");
          Assert.True(File.Exists(script), $"gate script missing: {script}");

          var psi = new ProcessStartInfo
          {
              FileName = "pwsh",
              ArgumentsList = { "-NoProfile", "-File", script },
              WorkingDirectory = repoRoot,
              RedirectStandardOutput = true,
              RedirectStandardError = true,
              UseShellExecute = false,
          };
          Process p;
          try { p = Process.Start(psi)!; }
          catch (System.ComponentModel.Win32Exception)
          {
              psi.FileName = "powershell";
              p = Process.Start(psi)!;
          }
          p.WaitForExit(60_000);
          var stdout = p.StandardOutput.ReadToEnd();
          var stderr = p.StandardError.ReadToEnd();
          Assert.True(p.HasExited, "gate did not exit within 60s");
          Assert.True(0 == p.ExitCode,
              $"threshold-public-surface-gate.ps1 exit={p.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
      }

      private static string FindRepoRoot()
      {
          var d = new DirectoryInfo(AppContext.BaseDirectory);
          while (d is not null && !File.Exists(Path.Combine(d.FullName, "POE2Radar.slnx")))
              d = d.Parent;
          Assert.NotNull(d);
          return d!.FullName;
      }
  }
  ```
  Then run it — expected FAIL with `gate script missing`:
  ```
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~ThresholdPublicSurfaceGateTests" --nologo -v minimal
  ```

- [ ] **Step 5: Write the public-surface grep gate script.**
  ```powershell
  # scripts/threshold-public-surface-gate.ps1
  # Threshold pre-tag public-surface gate.
  # Fails if any release-surface file leaks internal-tooling tokens
  # (Sikaka, GameHelper, Claude, superpowers, subagent, SDD, docs/superpowers/)
  # OUTSIDE the pre-existing legitimate-attribution allowlist.
  # Referenced from CLAUDE.md memory: feedback_no_internal_tooling_in_public_surfaces.
  # Also asserts CHANGELOG [Unreleased] Threshold block exists and README §Session HUD
  # mentions XP/hour.

  $ErrorActionPreference = 'Stop'
  $root = Split-Path -Parent $PSScriptRoot
  $violations = New-Object System.Collections.Generic.List[string]

  # --- Allowlist: (file, line) pairs where the token is legitimate pre-Threshold text.
  $allow = @{
      'README.md'                  = @(31, 166, 210, 226) # fork line, testing, docs, attribution
      'scripts/compliance-gate.ps1'= @(11)                # historical comment about upstream merges
      'NOTICE'                     = 'ALL'                # NOTICE file is legal attribution surface
      'CHANGELOG.md'               = @()                  # [Unreleased] Threshold block must be clean
  }

  $forbidden = @(
      'Sikaka','GameHelper','superpowers','subagent',
      'docs/superpowers/','Claude','\bSDD\b'
  )

  # Files to scan (release-surface only — NOT internal tooling).
  $scanFiles = @(
      'README.md',
      'CHANGELOG.md',
      'docs/pending-manual-steps.md'
  )
  # Plus every src/**/*.cs string literal added by Threshold — cheap approach: scan all src/**/*.cs.
  $scanFiles += Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter '*.cs' `
      | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\','/') }

  foreach ($rel in $scanFiles) {
      $full = Join-Path $root $rel
      if (-not (Test-Path $full)) { continue }
      $lines = Get-Content $full
      $allowLines = $allow[$rel]
      if ($allowLines -eq 'ALL') { continue }
      for ($i = 0; $i -lt $lines.Length; $i++) {
          $lineNo = $i + 1
          if ($allowLines -and ($allowLines -contains $lineNo)) { continue }
          foreach ($tok in $forbidden) {
              if ($lines[$i] -match $tok) {
                  # Filter: C# code files only flag string literals or comments (not identifiers).
                  if ($rel -like '*.cs') {
                      $lit = $lines[$i]
                      if ($lit -notmatch '"' -and $lit -notmatch '//' -and $lit -notmatch '/\*') {
                          continue
                      }
                  }
                  $violations.Add("$rel`:$lineNo`: forbidden token '$tok' -> $($lines[$i].Trim())")
              }
          }
      }
  }

  # --- Positive assertions on the Threshold docs shape.
  $cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw
  if ($cl -notmatch '(?m)^## \[Unreleased\] — v0\.22 "Threshold"') {
      $violations.Add("CHANGELOG.md: missing '## [Unreleased] — v0.22 \"Threshold\"' block")
  }
  $rm = Get-Content (Join-Path $root 'README.md') -Raw
  if ($rm -notmatch 'Session HUD[\s\S]{0,900}XP/hour') {
      $violations.Add("README.md: Session HUD bullet must mention XP/hour")
  }
  $pms = Get-Content (Join-Path $root 'docs/pending-manual-steps.md') -Raw
  if ($pms -match '(?m)^\| 6 \| \*\*XP field Research probe\*\*') {
      $violations.Add("docs/pending-manual-steps.md: PMS-6 must be moved out of Active table")
  }
  if ($pms -notmatch '2026-07-09 — Long List #34 XP/hour Session HUD chip') {
      $violations.Add("docs/pending-manual-steps.md: Done section missing PMS-6 closer entry")
  }

  if ($violations.Count -gt 0) {
      Write-Host "FAIL: threshold-public-surface-gate" -ForegroundColor Red
      $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
      exit 1
  }
  Write-Host "PASS: threshold-public-surface-gate" -ForegroundColor Green
  exit 0
  ```
  Save. Run standalone to see what's still missing:
  ```
  pwsh -NoProfile -File scripts/threshold-public-surface-gate.ps1
  ```
  Expected FAIL listing: (a) CHANGELOG missing Threshold block, (b) README Session HUD missing XP/hour, (c) PMS-6 still in Active.

- [ ] **Step 6: Edit `README.md:119` — replace the Session HUD bullet with the XP-augmented version. Exact byte-for-byte replacement (no other README edits).**
  Before (line 119):
  ```
  - ⏱️ **Session HUD** *(opt-in, off by default)* — a live run tracker: session + zone timers, zones visited, zones-per-hour pace, area name + level, a death counter (with a per-zone count), and **v2** metrics — **kills by rarity** (observed), **maps/hr**, and **XP-efficiency** (your level − area level). Each metric toggles independently and anchors to any screen corner; **Ctrl+Alt+R** resets the counters.
  ```
  After:
  ```
  - ⏱️ **Session HUD** *(opt-in, off by default)* — a live run tracker: session + zone timers, zones visited, zones-per-hour pace, area name + level, a death counter (with a per-zone count), and **v2** metrics — **kills by rarity** (observed), **maps/hr**, and **XP-efficiency** (your level − area level). New in this release: an **XP/hour** row on the HUD, opt-in and off by default, backed by a rolling window you can dial from **1 to 60 minutes** in Settings (default 5). It reads the same character-XP field the campaign tracker already uses, respects **Exclude Towns From Pace** so hideout time doesn't tank the rate, and prints a `(Nm to L##)` estimate off the built-in level curve when it has enough samples. Each metric toggles independently and anchors to any screen corner; **Ctrl+Alt+R** resets the counters.
  ```

- [ ] **Step 7: Edit `CHANGELOG.md:6` — insert a new `## [Unreleased] — v0.22 "Threshold"` block ABOVE the existing v0.21 Guided Campaign block. Both stay `[Unreleased]` in parallel per spec §1. Themed body per the release-routine memory (`feedback_release_themed_notes`).**
  Insert this block between the current line 4 (blank) and current line 6 (`## [Unreleased] — v0.21 "Guided Campaign"`):
  ```
  ## [Unreleased] — v0.22 "Threshold"

  ### Added — 🚪 **Threshold** *(waygates render as waygates · XP/hour lands on the Session HUD · monolith panel collapses when you're done reading it)*

  - 📈 **XP/hour on the Session HUD.** *(opt-in, off by default)* A new **XP/hr** row lives inside the existing Session HUD panel — no new panel, no new hotkey. Rolling window is user-tunable from **1 to 60 minutes** (default 5) via ⚙️ Settings → Session HUD → **XP window (min)**, mirrored in `/api/settings` as `sessionHudShowXpRate` + `sessionHudXpWindowMinutes`. Ring survives zone crossings (grind metric, not zone metric); town frames don't append — reuses the existing **Exclude Towns From Pace** toggle so hideout time doesn't drag the rate. **Ctrl+Alt+R** resets it alongside the rest of the HUD. When the window has enough samples the row prints `(Nm to L##)` off the built-in level curve. **Zero-cost when off:** with the row disabled, the fallback character-XP read is skipped entirely — a spy test locks the guarantee for 1000 disabled ticks.
  - 🚪 **Waygates render as tracked landmarks.** Built-in Tile display rule ships for the end-game `WaygateDevice` entity — Navigable, Eye-shape marker, distinct colour — so waygates surface on the radar the moment they enter range. Idempotent one-shot migration (`built_in_tile_rules_v1`) folded into the `AppliedMigrations` list; upgrading from v0.21 seeds the rule once, additive-only, no state loss.
  - 🩹 **Atlas content-icon draw fix.** Content-icons stamped on fogged atlas nodes (Breach / Boss / Essence / Expedition / …) were mis-rendering their destination rect at high zoom levels — one axis of the square was pulling the wrong dimension. Pattern-matched port straightens the rect so icons stay pixel-aligned to their node at every zoom.
  - 🗂️ **Click-to-collapse nearby-monolith reward panel.** New caret on the panel's title row toggles a persisted collapsed state; the reward rows hide, the title stays. `MonolithsTop` pre-sort/cap-to-6 semantics preserved — POE2GPS's monolith prioritization is untouched by the collapse port.

  ### Fixed
  - Nothing user-visible beyond the atlas-icon rect above.

  ### Compliance
  - 🛡️ **100% read-only.** Zero new memory writes. Zero new offset writes. Zero new input paths. Every new setting respects zero-cost-when-off. Pre-tag `scripts/threshold-public-surface-gate.ps1` locks the release surface — README, CHANGELOG, in-app strings — against internal-tooling leaks.

  ### Pending manual steps closed
  - **PMS-6 (Long List #34, XP/hour Session HUD chip)** — closed by this drop. See `docs/pending-manual-steps.md` Done section.

  ```
  Result: `## [Unreleased] — v0.22 "Threshold"` block sits above `## [Unreleased] — v0.21 "Guided Campaign"`.

- [ ] **Step 8: Edit `docs/pending-manual-steps.md` — remove row 6 from the Active table and append the Done entry.**
  a) Delete this line from the Active table (currently line 21):
  ```
  | 6 | **XP field Research probe** | Ships the deferred XP/hour Session HUD chip | Run `Research --xp`; verify the `Experience` int64 tracks across kills without character reload | 20-30 min | Long List #34 (XP/hour) |
  ```
  b) Append after the last existing Done entry (currently line 33):
  ```
  - **2026-07-09 — Long List #34 XP/hour Session HUD chip** shipped in v0.22 "Threshold" (unblocks PMS-6). Ring-buffered `SessionTracker.XpPerHour` (1-60 min window, default 5), fallback `Poe2Live.PlayerExperience(nint) → long` read shipped alongside Campaign Probe as the free-rider XP accessor, `PoE2XpCurveLoader` reuses the embedded `xp_curve.json` under `Campaign/Guide/Data/poe2/` — no duplicate level table. Opt-in, off by default. Reset piggybacks on **Ctrl+Alt+R**; town frames excluded via existing **Exclude Towns From Pace** knob. Zero-cost-when-off gate locked by spy test in `RadarApp.WorldTick`. No standalone Research probe run required — the accessor rides on the shipped Campaign Probe path.
  ```

- [ ] **Step 9: Re-run the standalone grep gate — expect PASS.**
  ```
  pwsh -NoProfile -File scripts/threshold-public-surface-gate.ps1
  ```
  Expected: `PASS: threshold-public-surface-gate` and exit code 0. If any violation prints, resolve it by (a) fixing the docs edit, or (b) if it's a false positive on legitimate pre-existing attribution, adding the exact line number to the `$allow` map in the script.

- [ ] **Step 10: Run the two new xunit tests — expect PASS.**
  ```
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --filter "FullyQualifiedName~ThresholdPublicSurfaceGateTests|FullyQualifiedName~XpHudFullSuiteSmokeTests" --nologo -v minimal
  ```
  Expected: `Passed: 8` (1 gate fact + 7 reflection facts).

- [ ] **Step 11: Run the FULL test suite — expect PASS with every Task 1-8 test still green.**
  ```
  dotnet test tests/POE2Radar.Tests/POE2Radar.Tests.csproj --nologo -v minimal
  ```
  Expected: `Passed!` with zero failures.

- [ ] **Step 12: Grep the diff for the hard non-negotiables of spec §2 (must show zero hits).**
  ```
  git diff --unified=0 -- 'src/**/*.cs' | grep -E 'MemoryReader\.Write|Marshal\.Write|SendInput|keybd_event|mouse_event|WriteProcessMemory|VirtualProtect|PostMessage\(WM_(KEY|MOUSE|LBUTTON)' && echo 'FAIL: write/input introduced' && exit 1 || echo 'OK: no write/input paths'
  git diff --unified=0 | grep -nE 'TODO|FIXME|HACK|XXX' && echo 'FAIL: TODO/HACK/FIXME/XXX in new code' && exit 1 || echo 'OK: no TODO/HACK'
  git diff --unified=0 | grep -n 'docs/superpowers/' && echo 'FAIL: superpowers path in code' && exit 1 || echo 'OK: no superpowers/ paths'
  ```
  Expected: three `OK:` lines. Any FAIL means the offending task shipped a violation — trace it back and fix at source, never inline-suppress here.

- [ ] **Step 13: Stage the Task 9 files.**
  ```
  git add README.md CHANGELOG.md docs/pending-manual-steps.md \
          scripts/threshold-public-surface-gate.ps1 \
          tests/POE2Radar.Tests/ThresholdPublicSurfaceGateTests.cs \
          tests/POE2Radar.Tests/Session/XpHudFullSuiteSmokeTests.cs
  git status
  ```
  Expected: exactly those six paths staged; nothing else drifting in.

- [ ] **Step 14: Commit — closes THR-XP-TESTS-DOCS and the Threshold feature branch's SDD phase.**
  ```
  git commit -m "$(cat <<'EOF'
  THR-XP-TESTS-DOCS: XP HUD test consolidation + Threshold public-surface docs

  - CHANGELOG: add [Unreleased] v0.22 "Threshold" themed block above v0.21
    (3 upstream fixes + XP HUD chip + PMS-6 closer + read-only compliance line).
  - README: Session HUD bullet gains XP/hour clause (opt-in default OFF,
    1-60 min window, respects Exclude Towns From Pace).
  - PMS: move row 6 (XP field Research probe) from Active to Done — Long List
    #34 closed by Threshold via Poe2Live.PlayerExperience + new SessionHud row.
  - New pre-tag public-surface gate scripts/threshold-public-surface-gate.ps1
    scoped to release-surface files (README, CHANGELOG, docs/pending-manual-steps.md,
    src/**/*.cs string literals) with an allowlist for pre-existing legitimate
    upstream attribution lines.
  - Reflection smoke locks the produced shapes of Tasks 1-8 (curve loader,
    9-arg Update overload, XpPerHour, Reset, ShowXpRate/XpWindowMinutes,
    BuiltInTileRules WaygateDevice, PanelCollapsed, defensive Map entry) so a
    silent revert of any upstream task trips a failing test at the true owner.

  Full suite green; grep gate green; §2 non-negotiables (zero writes / zero
  input paths / no TODO / no superpowers/ paths) diff-verified.
  EOF
  )"
  git status
  ```
  Expected: clean tree, `nothing to commit, working tree clean`, one new commit on `feat/threshold`. Feature branch ready for whole-branch review + `--no-ff` merge per spec §9.

---

## Ordering Constraints

1. **Tasks 1, 2, 4** (THR-DRAW-FIX, THR-WAYGATE-RULE, THR-MONO-COLLAPSE) — INDEPENDENT; parallelizable if executor has bandwidth.
2. **Task 3** (THR-WAYGATE-TESTS) — after Task 2 (consumes `BuiltInTileRules()` + `SeedBuiltInTileRulesIfNeeded`).
3. **Task 5** (THR-XP-CURVE-LOADER) — independent, no consumers before.
4. **Task 6** (THR-XP-TRACKER) — after Task 5 (consumes `PoE2XpCurveLoader.XpToNextLevel` + `TimeToNextLevel`).
5. **Task 7** (THR-XP-SETTINGS) — parallel with Task 6 (independent files).
6. **Task 8** (THR-XP-RENDER) — after Tasks 5, 6, 7 (consumes loader + tracker `SessionStats` + settings).
7. **Task 9** (THR-XP-TESTS-DOCS) — LAST; consolidates all shipped code + CHANGELOG themed body + PMS-6 tracker move to Done.

## Non-Goals (from spec §7)

- No R5 atlas_maps.json / atlas_content.json / AtlasIcons refresh (deferred to followup AtlasRefresh drop).
- No PriceBook MinQuantity fix (ground-loot pricing surface fully stripped — confirmed).
- No `/api/session-reset` HTTP route (deferred to dedicated dashboard UX drop).
- No R5 Waygate atlas-landmark family port (defer to atlas-refresh drop).
- No Currency Exchange (compliance).
- No HoverPrice tooltip (out of scope for navigation fork).
- No new hotkeys (Reset reuses Ctrl+Alt+R).
- No new panels (XP row lives inside existing `SessionHud`).
- **No duplicate XP curve** (dedup non-negotiable — reuse `Campaign/Guide/Data/poe2/xp_curve.json`).
- **No hard `SessionTracker.Update` signature change** (overload instead).
- No new memory-read cadence (piggyback on existing `_levelRefreshTick`).
