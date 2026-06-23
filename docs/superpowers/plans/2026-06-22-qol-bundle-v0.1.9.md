# POE2GPS v0.1.9 — QoL Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v0.1.9 — a menu-toggle chord, a monolith-panel default-off toggle, and a Gear Scorer v2 that un-breaks scoring with meta-derived starter weights + UI love.

**Architecture:** Three independent QoL changes that share no code, sequenced menu chord → monolith toggle → gear v2. Pure logic (controller chord, scorer normalization, the rendered-line index, starter weights) lives in `POE2Radar.Core` and is unit-tested; the overlay/API/dashboard get thin hooks. The starter weight set is generated **offline** (a `POE2Radar.Research --gen-weights` probe) from a vendored Tincture ladder snapshot and embedded as a JSON resource.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), xUnit (`tests/POE2Radar.Tests`, references Core only), Vortice.Direct2D1 (overlay), vanilla JS dashboard embedded as a C# string in `DashboardHtml.cs`.

## Global Constraints

- **.NET 10, `net10.0-windows`, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` — every task ends with **0 warnings, 0 errors**.
- **Strictly read-only / GGG-compliant.** No `SendInput`/`PostMessage`/`keybd_event`/`mouse_event`; no `WriteProcessMemory`/`VirtualProtectEx`/injection; no pricing layer. `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1` must print **PASS**. New input is read-only (XInput button reads, `GetAsyncKeyState`) and foreground-gated.
- **Syncability.** New logic in new/owned files; only thin hooks into the high-churn shared files (`RadarApp.cs`, `ApiServer.cs`, `DashboardHtml.cs`, `RadarSettings.cs`, `RenderContext.cs`). Do not restructure them. See `docs/upstream-merge.md`.
- **No identifying data** in any API payload (no character name, no raw addresses).
- **Tests reference `POE2Radar.Core` only** — pure logic that needs a unit test lives in Core.
- Build: `dotnet build POE2Radar.slnx`. Test: `dotnet test POE2Radar.slnx`. Gate: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`.
- Data provenance: the Tincture snapshot is vendored frozen + credited (no stated license; fan poe.ninja aggregation). PoB's GPLv3 Lua is **not** vendored (the tier fast-follow, out of scope here, will source ranges from RePoE).

---

## File Structure

**New files:**
- `src/POE2Radar.Core/Input/ControllerChord.cs` — pure `ControllerInput` record + `Resolve(prev,cur)` edge seam (Task 1).
- `src/POE2Radar.Core/Game/StarterWeights.cs` — loads the embedded `starter_stat_weights.json` (Task 7).
- `src/POE2Radar.Core/Game/starter_stat_weights.json` — generated, committed meta-derived starter table (Task 6).
- `resources/poe2-data/tincture-meta-detail.json` — vendored frozen Tincture snapshot (Task 6).
- `tests/POE2Radar.Tests/ControllerChordTests.cs` (Task 1), `GearScorerNormTests.cs` (Task 4), `RenderedLineIndexTests.cs` (Task 5), `StarterWeightsTests.cs` (Task 7).

**Modified (thin hooks):**
- `src/POE2Radar.Overlay/Input/ControllerCycler.cs` — return `ControllerInput` via the seam (Task 2).
- `src/POE2Radar.Overlay/RadarApp.cs` — HandleHotkeys controller+keyboard menu toggle, `_nextMenuAt`, console branding, `GearJson` statIds (Tasks 2, 9).
- `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` — chip text → "POE2GPS" (Task 2).
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — `MonolithSettings.ShowPanel` default false (Task 3).
- `src/POE2Radar.Overlay/Web/ApiServer.cs` — `showMonolithPanel` round-trip (Task 3); gear-weights `reset`/`norm` (Task 9).
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` — monolith toggle row (Task 3); gear tab colors/chips/starter button/grid (Tasks 10, 11).
- `src/POE2Radar.Core/Game/ItemModTranslator.cs` — `StatIdsForRenderedLine` reverse index (Task 5).
- `src/POE2Radar.Core/Gear/GearScorer.cs` — `AffixContribution.StatIds`, `StatWeights.NormById`, normalized contribution (Task 4).
- `src/POE2Radar.Core/POE2Radar.Core.csproj` — embed `starter_stat_weights.json` (Task 6).
- `src/POE2Radar.Research/Program.cs` — `--gen-weights` offline probe (Task 6).
- `src/POE2Radar.Overlay/Web/GearWeightStore.cs` — NormById + starter seeding + `LoadStarter()` (Task 8).
- `src/POE2Radar.Overlay/GearSnapshot.cs` — (only if statIds need threading; see Task 9).

---

## Part 1 — Menu toggle chord

### Task 1: Pure controller-chord seam (Core) + tests

**Files:**
- Create: `src/POE2Radar.Core/Input/ControllerChord.cs`
- Test: `tests/POE2Radar.Tests/ControllerChordTests.cs`

**Interfaces:**
- Produces: `POE2Radar.Core.Input.ControllerInput(int Cycle, bool MenuToggle)` and `ControllerChord.Resolve(ushort prev, ushort cur) -> ControllerInput`, plus `ControllerChord.LeftThumb`/`RightThumb` consts. Task 2 consumes these.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/POE2Radar.Tests/ControllerChordTests.cs
using POE2Radar.Core.Input;
using Xunit;

namespace POE2Radar.Tests;

public class ControllerChordTests
{
    private const ushort L = ControllerChord.LeftThumb;   // 0x0040
    private const ushort R = ControllerChord.RightThumb;  // 0x0080

    [Fact] public void L3_rising_edge_is_prev()  => Assert.Equal(new ControllerInput(-1, false), ControllerChord.Resolve(0, L));
    [Fact] public void R3_rising_edge_is_next()  => Assert.Equal(new ControllerInput(+1, false), ControllerChord.Resolve(0, R));
    [Fact] public void L3_held_does_not_repeat() => Assert.Equal(default, ControllerChord.Resolve(L, L));

    [Fact] public void both_down_rising_edge_toggles_menu_and_suppresses_cycle()
        => Assert.Equal(new ControllerInput(0, true), ControllerChord.Resolve(L, (ushort)(L | R)));

    [Fact] public void both_down_held_does_not_repeat_toggle()
        => Assert.Equal(new ControllerInput(0, false), ControllerChord.Resolve((ushort)(L | R), (ushort)(L | R)));

    [Fact] public void releasing_one_of_two_held_emits_nothing()
        => Assert.Equal(default, ControllerChord.Resolve((ushort)(L | R), R));

    [Fact] public void no_change_emits_default()
        => Assert.Equal(default, ControllerChord.Resolve(0, 0));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx --filter ControllerChordTests`
Expected: FAIL — `ControllerChord`/`ControllerInput` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/POE2Radar.Core/Input/ControllerChord.cs
namespace POE2Radar.Core.Input;

/// <summary>One controller poll's resolved intent: a cycle direction (-1 prev / +1 next / 0 none) and
/// whether the L3+R3 menu-toggle chord fired this poll. Pure + deterministic so it is unit-testable.</summary>
public readonly record struct ControllerInput(int Cycle, bool MenuToggle);

/// <summary>Pure edge resolver shared by the Quick-Target cycler and the nav-menu chord. Given the
/// previous and current XInput button masks: L3+R3 held together fires <see cref="ControllerInput.MenuToggle"/>
/// once (on the rising edge of "both down") and suppresses the single-stick cycle for that press; otherwise a
/// rising edge of L3 = -1 (prev) or R3 = +1 (next). Read-only — never emits input.</summary>
public static class ControllerChord
{
    public const ushort LeftThumb = 0x0040;   // XInput GAMEPAD_LEFT_THUMB  (L3)
    public const ushort RightThumb = 0x0080;  // XInput GAMEPAD_RIGHT_THUMB (R3)

    public static ControllerInput Resolve(ushort prev, ushort cur)
    {
        var bothNow = (cur & LeftThumb) != 0 && (cur & RightThumb) != 0;
        if (bothNow)
        {
            var bothPrev = (prev & LeftThumb) != 0 && (prev & RightThumb) != 0;
            return new ControllerInput(0, !bothPrev);   // toggle once on rising edge; suppress cycle while both held
        }

        var pressed = (ushort)(cur & ~prev);            // rising edges since last poll
        if ((pressed & LeftThumb) != 0) return new ControllerInput(-1, false);
        if ((pressed & RightThumb) != 0) return new ControllerInput(+1, false);
        return default;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx --filter ControllerChordTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Input/ControllerChord.cs tests/POE2Radar.Tests/ControllerChordTests.cs
git commit -m "feat(core): pure ControllerChord seam (L3+R3 menu chord + cycle) with tests"
```

---

### Task 2: Wire the chord + Ctrl+Alt+M + branding fixes (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Input/ControllerCycler.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`_nextMenuAt` field ~247; HandleHotkeys controller block 1339-1346; new keyboard block; console branding line 534)
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs:848` (chip text)

**Interfaces:**
- Consumes: `ControllerChord`/`ControllerInput` from Task 1.
- Produces: `ControllerCycler.Poll()` now returns `POE2Radar.Core.Input.ControllerInput`.

- [ ] **Step 1: Rewrite `ControllerCycler` to return `ControllerInput` via the seam**

Replace the whole body of `src/POE2Radar.Overlay/Input/ControllerCycler.cs` with:

```csharp
using POE2Radar.Core.Input;

namespace POE2Radar.Overlay.Input;

/// <summary>Polls XInput controller 0 and resolves L3/R3 into a <see cref="ControllerInput"/> via the pure
/// <see cref="ControllerChord"/> seam: L3=prev, R3=next, L3+R3=menu toggle. Read-only — never emits input.</summary>
internal sealed class ControllerCycler
{
    private ushort _prev;

    /// <summary>Poll once. Returns the resolved cycle direction + menu-toggle edge. Always call each frame
    /// so the edge state stays correct.</summary>
    public ControllerInput Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return default; }
        var result = ControllerChord.Resolve(_prev, cur);
        _prev = cur;
        return result;
    }
}
```

- [ ] **Step 2: Add the `_nextMenuAt` debounce field in RadarApp.cs**

Find (around line 245-247):

```csharp
    // ── Collapsible "POE2Radar" navigation menu widget state (drawn always-on; persisted corner). ──
    private bool _navMenuExpanded;                                       // dropdown open? (default collapsed)
```

Replace with:

```csharp
    // ── Collapsible "POE2GPS" navigation menu widget state (drawn always-on; persisted corner). ──
    private bool _navMenuExpanded;                                       // dropdown open? (default collapsed)
    private DateTime _nextMenuAt = DateTime.MinValue;                    // Ctrl+Alt+M menu-toggle debounce
```

- [ ] **Step 3: Replace the controller-cycler block in HandleHotkeys to consume `ControllerInput`**

Find (RadarApp.cs ~1339-1346):

```csharp
        // Quick-Target Cycler (controller): L3 = prev, R3 = next. Poll every frame to keep edge state
        // fresh; only ACT while PoE2 is foreground. Read-only XInput — sends nothing to the game.
        if (_settings.EnableControllerCycle)
        {
            var dir = _controllerCycler.Poll();
            if (dir != 0 && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd)
                Cycle(dir < 0 ? CycleAction.Prev : CycleAction.Next);
        }
```

Replace with:

```csharp
        // Quick-Target Cycler + nav-menu chord (controller): L3 = prev, R3 = next, L3+R3 = toggle the nav
        // menu. Poll every frame to keep edge state fresh; only ACT while PoE2 is foreground. The chord
        // suppresses the single-stick cycle so opening the menu never also flips the active target.
        // Read-only XInput — sends nothing to the game.
        if (_settings.EnableControllerCycle)
        {
            var input = _controllerCycler.Poll();
            if (_gameHwnd != 0 && GetForegroundWindow() == _gameHwnd)
            {
                if (input.MenuToggle) _navMenuExpanded = !_navMenuExpanded;
                else if (input.Cycle != 0) Cycle(input.Cycle < 0 ? CycleAction.Prev : CycleAction.Next);
            }
        }
```

Add `using POE2Radar.Core.Input;` to the top of `RadarApp.cs` if not already present (needed for `ControllerInput` member access — verify with build; the type flows through `Poll()`).

- [ ] **Step 4: Add the keyboard menu-toggle (Ctrl+Alt+M) just above the controller block**

Immediately BEFORE the controller block from Step 3, insert:

```csharp
        // Nav-menu toggle (keyboard): Ctrl+Alt+M flips the top-left nav-menu dropdown. Foreground-gated +
        // debounced. Reads keys only — sends nothing to the game.
        if (_settings.EnableTargetHotkeys && DateTime.UtcNow >= _nextMenuAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
            && Down(0x11) && Down(0x12) && Down(0x4D))   // Ctrl + Alt + M
        {
            _navMenuExpanded = !_navMenuExpanded;
            _nextMenuAt = DateTime.UtcNow.AddMilliseconds(300);
        }
```

- [ ] **Step 5: Fix the two "POE2Radar" branding strings**

In `RadarApp.cs` (line ~534), change:

```csharp
                    Console.WriteLine($"POE2Radar v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
```
to:
```csharp
                    Console.WriteLine($"POE2GPS v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
```

In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (line ~848), change:

```csharp
        const string chip = "POE2Radar";
```
to:
```csharp
        const string chip = "POE2GPS";
```

- [ ] **Step 6: Build + gate**

Run: `dotnet build POE2Radar.slnx`
Expected: 0 warnings, 0 errors.
Run: `powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/POE2Radar.Overlay/Input/ControllerCycler.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(overlay): L3+R3 / Ctrl+Alt+M toggle the nav menu; chip+console branding -> POE2GPS"
```

---

## Part 2 — Monolith reward panel: default off + Settings toggle

### Task 3: Default off + flat `showMonolithPanel` round-trip + dashboard toggle

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (`MonolithSettings.ShowPanel`, ~line 391)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`ReadSettings()` ~589-618; `ApplySettings` ~620-676)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (Settings tab "Radar Display" card ~516)

**Interfaces:** flat settings key `showMonolithPanel` (bool) ↔ `_settings.Monoliths.ShowPanel`.

- [ ] **Step 1: Flip the default**

In `RadarSettings.cs` (~line 391) change:

```csharp
    public bool ShowPanel { get; set; } = true;          // the in-overlay nearby-monolith reward panel
```
to:
```csharp
    public bool ShowPanel { get; set; } = false;         // the in-overlay nearby-monolith reward panel (off by default; toggle in Settings)
```

- [ ] **Step 2: Expose it in the GET projection**

In `ApiServer.cs` `ReadSettings()` (after the `enableControllerCycle = _settings.EnableControllerCycle,` line), add:

```csharp
        showMonolithPanel = _settings.Monoliths.ShowPanel,
```

- [ ] **Step 3: Accept it in ApplySettings**

In `ApiServer.cs` `ApplySettings`, alongside the other flat `when TryBool` cases (e.g. after the `enableControllerCycle` case), add:

```csharp
                case "showMonolithPanel" when TryBool(p.Value, out var b): _settings.Monoliths.ShowPanel = b; applied.Add(p.Name); break;
```

- [ ] **Step 4: Add the dashboard toggle row**

In `DashboardHtml.cs`, inside the `data-view="settings"` Radar Display card (next to the existing `showTerrain`/`showPlayerBlip` rows ~518-535), add:

```html
            <div class="row"><div class="rl">Monolith reward panel<small>the nearby-monolith reward list drawn over the minimap (off by default)</small></div>
              <label class="sw"><input type="checkbox" data-set="showMonolithPanel"><span class="track"></span><span class="knob"></span></label></div>
```

(The generic `[data-set]` binder in `loadSettings`/`wireSettings` wires it automatically — no JS change.)

- [ ] **Step 5: Build + gate + manual verify**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Run the gate → PASS.
Manual (live, after build): open `http://localhost:7777` → Settings → toggle "Monolith reward panel" off/on with a monolith nearby → the panel disappears/reappears next frame. Confirm a fresh `config/` shows the panel **off** by default.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat: monolith reward panel off by default + Settings toggle (showMonolithPanel round-trip)"
```

---

## Part 3 — Gear Scorer v2

### Task 4: Scorer model — `AffixContribution.StatIds` + `StatWeights.NormById` + normalized contribution (Core) + tests

**Files:**
- Modify: `src/POE2Radar.Core/Gear/GearScorer.cs`
- Test: `tests/POE2Radar.Tests/GearScorerNormTests.cs`
- (Touch: any existing `GearScorer` tests that construct `AffixContribution` — update to the new shape.)

**Interfaces:**
- Produces: `AffixContribution(string Line, IReadOnlyList<string> StatIds, double Value, double Weight, double Points)`; `StatWeights(IReadOnlyDictionary<string,double> ByStatId, double Target, double GodRollThreshold, IReadOnlyDictionary<string,double>? NormById = null)`; scoring uses `(value / norm) × weight` with `norm` defaulting to 1.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/POE2Radar.Tests/GearScorerNormTests.cs
using POE2Radar.Core.Gear;
using Xunit;

namespace POE2Radar.Tests;

public class GearScorerNormTests
{
    private static Affix A(string id, double v) => new("line", new[] { id }, v);

    [Fact]
    public void norm_present_divides_value_before_weighting()
    {
        // life rolled 100, norm (median) 115, weight (pct) 20 → contribution = 100/115*20 ≈ 17.39 → score ≈ 17.39
        var w = new StatWeights(new Dictionary<string, double> { ["base_maximum_life"] = 20 }, 100, 85,
            new Dictionary<string, double> { ["base_maximum_life"] = 115 });
        var s = GearScorer.Score(new[] { A("base_maximum_life", 100) }, w);
        Assert.InRange(s.Score, 17.0, 17.8);
        Assert.Equal("base_maximum_life", Assert.Single(s.Affixes[0].StatIds));
    }

    [Fact]
    public void norm_absent_falls_back_to_times_one()
    {
        // no NormById → norm=1 → contribution = value*weight = 2*5 = 10
        var w = new StatWeights(new Dictionary<string, double> { ["x"] = 5 }, 100, 85);
        var s = GearScorer.Score(new[] { A("x", 2) }, w);
        Assert.InRange(s.Score, 9.9, 10.1);
    }

    [Fact]
    public void meta_weighted_affix_scores_nonzero()  // the un-break
    {
        var w = new StatWeights(new Dictionary<string, double> { ["base_maximum_life"] = 20 }, 100, 85,
            new Dictionary<string, double> { ["base_maximum_life"] = 115 });
        Assert.True(GearScorer.Score(new[] { A("base_maximum_life", 120) }, w).Score > 0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx --filter GearScorerNormTests`
Expected: FAIL — `StatWeights` has no `NormById`, `AffixContribution` has no `StatIds`.

- [ ] **Step 3: Update `GearScorer.cs`**

Change the records + the `Score`/`WeightFor` logic:

```csharp
public sealed record StatWeights(
    IReadOnlyDictionary<string, double> ByStatId,
    double Target,
    double GodRollThreshold,
    IReadOnlyDictionary<string, double>? NormById = null);

/// <summary>One affix's contribution to the score (for the dashboard breakdown).</summary>
public sealed record AffixContribution(string Line, IReadOnlyList<string> StatIds, double Value, double Weight, double Points);
```

In `Score`, change the per-affix loop so it normalizes and records stat ids:

```csharp
        foreach (var a in affixes)
        {
            var weight = WeightFor(a.StatIds, weights.ByStatId);
            var norm = NormFor(a.StatIds, weights.NormById);
            var points = a.Value / norm * weight;
            raw += points;
            contributions.Add(new AffixContribution(a.StatLine, a.StatIds, a.Value, weight, points));
        }
```

Add the `NormFor` helper next to `WeightFor` (the per-stat normalization denominator; max-weight stat id wins, mirroring `WeightFor`; defaults to 1):

```csharp
    /// <summary>The normalization denominator for this affix: the norm of the SAME stat id that supplied the
    /// max weight, or 1 when no norm is configured. Keeps big-number stats (Life ~115) comparable to small
    /// ones (resist ~37).</summary>
    private static double NormFor(IReadOnlyList<string> statIds, IReadOnlyDictionary<string, double>? normById)
    {
        if (normById == null) return 1.0;
        double best = 1.0;
        for (var i = 0; i < statIds.Count; i++)
            if (normById.TryGetValue(statIds[i], out var n) && n > 0)
                return n;   // first configured norm wins (stat ids are positional; one per affix in practice)
        return best;
    }
```

(`WeightFor` is unchanged.)

- [ ] **Step 4: Update the existing GearScorer test that constructs `AffixContribution`**

Search the test project for `AffixContribution(` and `new StatWeights(`; update any call sites to the new positional shape (add the `StatIds` arg / leave `NormById` defaulted). Run the search:

Run: `git grep -n "AffixContribution(\|new StatWeights(" tests/`
Fix each to compile against the new records (the existing un-normalized tests still pass because `NormById` defaults to null → ×1).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test POE2Radar.slnx --filter Gear`
Expected: PASS (new norm tests + existing GearScorer tests).

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Core/Gear/GearScorer.cs tests/POE2Radar.Tests/
git commit -m "feat(core): gear scorer per-stat normalization (NormById) + expose affix StatIds"
```

---

### Task 5: `ItemModTranslator.StatIdsForRenderedLine` reverse index (Core) + test

**Files:**
- Modify: `src/POE2Radar.Core/Game/ItemModTranslator.cs`
- Test: `tests/POE2Radar.Tests/RenderedLineIndexTests.cs`

**Interfaces:**
- Produces: `public string[]? ItemModTranslator.StatIdsForRenderedLine(string line)` — maps a value-templated stat line (e.g. `"+# to maximum Life"`) to its stat ids. Task 6 consumes it.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/POE2Radar.Tests/RenderedLineIndexTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class RenderedLineIndexTests
{
    [Theory]
    [InlineData("+# to maximum Life", "base_maximum_life")]
    [InlineData("+#% to Cold Resistance", "base_cold_damage_resistance_%")]
    public void resolves_common_meta_lines(string line, string expectedStatId)
    {
        var ids = ItemModTranslator.Shared.StatIdsForRenderedLine(line);
        Assert.NotNull(ids);
        Assert.Contains(expectedStatId, ids!);
    }

    [Fact]
    public void unknown_line_returns_null()
        => Assert.Null(ItemModTranslator.Shared.StatIdsForRenderedLine("this is not a real stat line at all"));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx --filter RenderedLineIndexTests`
Expected: FAIL — `StatIdsForRenderedLine` does not exist.

- [ ] **Step 3: Add the reverse index to `ItemModTranslator.cs`**

Add a cached reverse index + the public accessor (place near `StatIdsFor`). It renders each embedded stat-description template with its values left as the **format token** (`"+#"` / `"#"`, or removed for `"ignore"`), strips `[ref|display]` tags via the existing `CleanTags`, normalizes (lowercased, whitespace-collapsed), and maps the result → that entry's stat ids — the exact form Tincture's `gear[].name` lines take:

```csharp
    private Dictionary<string, string[]>? _renderedIndex;
    private readonly object _renderedIndexGate = new();

    /// <summary>The stat ids for a fully value-templated stat line (numbers shown as the format token, e.g.
    /// <c>"+# to maximum Life"</c> / <c>"#% increased Movement Speed"</c>), or null if unrecognized. Used by
    /// the offline starter-weights generator to bridge Tincture's meta stat lines to our stat ids.</summary>
    public string[]? StatIdsForRenderedLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        EnsureRenderedIndex();
        return _renderedIndex!.GetValueOrDefault(NormalizeLine(line));
    }

    private void EnsureRenderedIndex()
    {
        if (_renderedIndex != null) return;
        lock (_renderedIndexGate)
        {
            if (_renderedIndex != null) return;
            var idx = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var list in _byStat.Values)
            foreach (var entry in list)
            foreach (var rule in entry.English)
            {
                var s = rule.String ?? "";
                var fmt = rule.Format ?? Array.Empty<string>();
                for (var i = 0; i < fmt.Length; i++)
                {
                    var token = "{" + i + "}";
                    s = fmt[i].Contains("ignore", StringComparison.Ordinal) ? s.Replace(token, "") : s.Replace(token, fmt[i]);
                }
                var key = NormalizeLine(CleanTags(s));
                if (key.Length > 0) idx.TryAdd(key, entry.Ids);
            }
            _renderedIndex = idx;
        }
    }

    private static string NormalizeLine(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
```

(`_byStat`, `CleanTags`, `StatDesc.English`/`.Format`/`.String`/`.Ids` already exist.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx --filter RenderedLineIndexTests`
Expected: PASS. (If `+# to maximum Life` fails to resolve, dump the index keys for `*maximum life*` and adjust `NormalizeLine` — but the research verified this exact line resolves.)

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Game/ItemModTranslator.cs tests/POE2Radar.Tests/RenderedLineIndexTests.cs
git commit -m "feat(core): ItemModTranslator.StatIdsForRenderedLine reverse index (meta-line -> stat ids)"
```

---

### Task 6: Vendor Tincture snapshot + `--gen-weights` offline probe → generate + embed `starter_stat_weights.json`

**Files:**
- Create: `resources/poe2-data/tincture-meta-detail.json` (vendored snapshot)
- Create: `src/POE2Radar.Core/Game/starter_stat_weights.json` (generated artifact)
- Modify: `src/POE2Radar.Core/POE2Radar.Core.csproj` (embed the artifact)
- Modify: `src/POE2Radar.Research/Program.cs` (dispatch `--gen-weights` ABOVE `AttachToPoE`; add `RunGenWeights`)

**Interfaces:**
- Consumes: `ItemModTranslator.StatIdsForRenderedLine` (Task 5).
- Produces: embedded `starter_stat_weights.json` shaped `{ "byStatId": {id: pct}, "normById": {id: (lo+hi)/2}, "target": 100, "godRollThreshold": 85 }`. Task 7 loads it.

- [ ] **Step 1: Vendor the Tincture snapshot**

```bash
mkdir -p resources/poe2-data
gh api repos/luther-rotmg/Tincture/contents/meta-detail.json -H "Accept: application/vnd.github.raw" > resources/poe2-data/tincture-meta-detail.json
```

Add a one-line credit at the top of `resources/poe2-data/README.md` (create if absent): `tincture-meta-detail.json — vendored snapshot of luther-rotmg/Tincture (PoE2 ladder meta; fan poe.ninja aggregation). Regenerate per patch.`

- [ ] **Step 2: Add the `--gen-weights` dispatch ABOVE `AttachToPoE`**

In `src/POE2Radar.Research/Program.cs`, immediately AFTER the two header `Console.WriteLine` calls (lines ~15-16, BEFORE `using var process = ProcessHandle.AttachToPoE();`), insert:

```csharp
// Offline generator (no live game): build the meta-derived starter weight table from a vendored Tincture
// meta-detail.json snapshot. Dispatched here, ABOVE AttachToPoE, so it runs without PoE2 running.
if (HasFlag(args, "--gen-weights"))
    return RunGenWeights(TryGetStrArg(args, "--meta"), TryGetStrArg(args, "--out"));
```

- [ ] **Step 3: Implement `RunGenWeights` (add near the other `static int Run...` methods)**

```csharp
static int RunGenWeights(string? metaPath, string? outPath)
{
    metaPath ??= "resources/poe2-data/tincture-meta-detail.json";
    outPath ??= "src/POE2Radar.Core/Game/starter_stat_weights.json";
    if (!System.IO.File.Exists(metaPath)) { Console.Error.WriteLine($"meta snapshot not found: {metaPath}"); return 1; }

    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(metaPath));
    if (!doc.RootElement.TryGetProperty("global", out var global) || !global.TryGetProperty("gear", out var gear))
    { Console.Error.WriteLine("meta-detail.json has no global.gear array."); return 1; }

    var byStatId = new Dictionary<string, double>(StringComparer.Ordinal);
    var normById = new Dictionary<string, double>(StringComparer.Ordinal);
    int matched = 0, total = 0;
    var unmatched = new List<string>();

    foreach (var g in gear.EnumerateArray())
    {
        total++;
        var name = g.GetProperty("name").GetString() ?? "";
        var pct = g.TryGetProperty("pct", out var p) && p.TryGetDouble(out var pv) ? pv : 0;
        double lo = g.TryGetProperty("lo", out var le) && le.TryGetDouble(out var lov) ? lov : 0;
        double hi = g.TryGetProperty("hi", out var he) && he.TryGetDouble(out var hiv) ? hiv : 0;
        var ids = POE2Radar.Core.Game.ItemModTranslator.Shared.StatIdsForRenderedLine(name);
        if (ids == null || ids.Length == 0) { unmatched.Add(name); continue; }
        matched++;
        var norm = (lo > 0 || hi > 0) ? (lo + hi) / 2.0 : 1.0;
        foreach (var id in ids)
        {
            if (pct > byStatId.GetValueOrDefault(id)) byStatId[id] = Math.Round(pct, 2);   // strongest meta signal wins
            if (norm > 0) normById[id] = Math.Round(norm, 2);
        }
    }

    var model = new { byStatId, normById, target = 100.0, godRollThreshold = 85.0 };
    var json = System.Text.Json.JsonSerializer.Serialize(model,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText(outPath, json);

    Console.WriteLine($"Generated {outPath}: {byStatId.Count} stat ids from {matched}/{total} meta gear lines.");
    if (unmatched.Count > 0) Console.WriteLine("Unmatched (left to hand-weight): " + string.Join(" | ", unmatched));
    return 0;
}
```

- [ ] **Step 4: Generate the artifact**

Run: `dotnet run --project src/POE2Radar.Research -- --gen-weights`
Expected: prints `Generated src/POE2Radar.Core/Game/starter_stat_weights.json: N stat ids from M/8 meta gear lines.` with M close to 8 (global.gear has 8 entries). Open the file and confirm it has real ids (e.g. `base_maximum_life`) with sensible weights + norms.

- [ ] **Step 5: Embed the generated artifact**

In `src/POE2Radar.Core/POE2Radar.Core.csproj`, in the `<ItemGroup>` of `<EmbeddedResource>`s (after `poe2_stat_descriptions.json`), add:

```xml
    <EmbeddedResource Include="Game\starter_stat_weights.json" />
```

- [ ] **Step 6: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E (TreatWarningsAsErrors: the new probe must be warning-clean — no unused locals). Run the gate → PASS.

- [ ] **Step 7: Commit (including the generated artifact + vendored snapshot)**

```bash
git add resources/poe2-data/ src/POE2Radar.Core/Game/starter_stat_weights.json src/POE2Radar.Core/POE2Radar.Core.csproj src/POE2Radar.Research/Program.cs
git commit -m "feat: --gen-weights offline probe + vendored Tincture snapshot -> embedded meta starter weights"
```

---

### Task 7: `StarterWeights` Core loader + test

**Files:**
- Create: `src/POE2Radar.Core/Game/StarterWeights.cs`
- Test: `tests/POE2Radar.Tests/StarterWeightsTests.cs`

**Interfaces:**
- Produces: `StarterWeights.ByStatId`, `StarterWeights.NormById` (both `IReadOnlyDictionary<string,double>`), `StarterWeights.Target`, `StarterWeights.GodRollThreshold` — loaded once from the embedded `starter_stat_weights.json`. Task 8 consumes it.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/POE2Radar.Tests/StarterWeightsTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class StarterWeightsTests
{
    [Fact]
    public void embedded_starter_weights_are_nonempty_and_include_life()
    {
        Assert.NotEmpty(StarterWeights.ByStatId);
        Assert.True(StarterWeights.ByStatId.TryGetValue("base_maximum_life", out var w) && w > 0,
            "expected a positive starter weight for base_maximum_life");
        Assert.True(StarterWeights.NormById.TryGetValue("base_maximum_life", out var n) && n > 0,
            "expected a positive norm for base_maximum_life");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx --filter StarterWeightsTests`
Expected: FAIL — `StarterWeights` does not exist.

- [ ] **Step 3: Implement the loader (mirrors `ItemModTranslator`'s `OpenRes` embed mechanism)**

```csharp
// src/POE2Radar.Core/Game/StarterWeights.cs
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>The meta-derived default gear-scoring weights, loaded once from the embedded
/// <c>starter_stat_weights.json</c> (generated offline from a Tincture ladder snapshot via
/// <c>POE2Radar.Research --gen-weights</c>). Read-only; the user can override per stat in the dashboard.</summary>
public static class StarterWeights
{
    private sealed class Model
    {
        [JsonPropertyName("byStatId")] public Dictionary<string, double> ByStatId { get; set; } = new();
        [JsonPropertyName("normById")] public Dictionary<string, double> NormById { get; set; } = new();
        [JsonPropertyName("target")] public double Target { get; set; } = 100;
        [JsonPropertyName("godRollThreshold")] public double GodRollThreshold { get; set; } = 85;
    }

    private static readonly Model M = Load();

    public static IReadOnlyDictionary<string, double> ByStatId => M.ByStatId;
    public static IReadOnlyDictionary<string, double> NormById => M.NormById;
    public static double Target => M.Target;
    public static double GodRollThreshold => M.GodRollThreshold;

    private static Model Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("starter_stat_weights", StringComparison.Ordinal));
            if (name == null) return new Model();
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return new Model();
            return JsonSerializer.Deserialize<Model>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Model();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StarterWeights load failed: {ex.Message}");
            return new Model();
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx --filter StarterWeightsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Game/StarterWeights.cs tests/POE2Radar.Tests/StarterWeightsTests.cs
git commit -m "feat(core): StarterWeights loader for the embedded meta starter weight table"
```

---

### Task 8: `GearWeightStore` — carry NormById, seed starter on absent file, `LoadStarter()` (Overlay)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/GearWeightStore.cs`

**Interfaces:**
- Consumes: `StarterWeights` (Task 7), `StatWeights.NormById` (Task 4).
- Produces: `GearWeightStore.Snapshot()` now includes `NormById`; `View()` now includes `norm`; new `LoadStarter()`; new `SetNorm(statId, norm)`.

- [ ] **Step 1: Add norm state + thread it through Snapshot/View/Model**

In `GearWeightStore.cs` add a norm dictionary beside `_byStatId`:

```csharp
    private Dictionary<string, double> _normById = new(StringComparer.OrdinalIgnoreCase); // under _gate
```

Update `Snapshot()` to pass norms to the scorer:

```csharp
    public StatWeights Snapshot()
    {
        lock (_gate)
            return new StatWeights(
                new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase), _target, _threshold,
                new Dictionary<string, double>(_normById, StringComparer.OrdinalIgnoreCase));
    }
```

Update `View()` to round-trip norms:

```csharp
    public object View()
    {
        lock (_gate)
            return new
            {
                byStatId = new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase),
                normById = new Dictionary<string, double>(_normById, StringComparer.OrdinalIgnoreCase),
                target = _target, godRollThreshold = _threshold,
            };
    }
```

Update the private `Model` class + `Load`/`Save` to persist `NormById`:

```csharp
    private sealed class Model
    {
        public Dictionary<string, double> ByStatId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> NormById { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double Target { get; set; } = 100;
        public double GodRollThreshold { get; set; } = 85;
    }
```

In `Load`, set `_normById = new Dictionary<string,double>(m.NormById, StringComparer.OrdinalIgnoreCase);`. In `Save`, add `NormById = _normById` to the `new Model { ... }`.

- [ ] **Step 2: Add `SetNorm` and `LoadStarter`; seed starter when the file is absent**

```csharp
    /// <summary>Set (or clear, when norm &lt;= 0) the per-stat normalization for a stat id; saves.</summary>
    public void SetNorm(string statId, double norm)
    {
        if (string.IsNullOrWhiteSpace(statId)) return;
        lock (_gate)
        {
            var k = statId.Trim();
            if (norm <= 0) _normById.Remove(k); else _normById[k] = norm;
            Save();
        }
    }

    /// <summary>Replace the current weights + norms with the embedded meta starter set; saves.</summary>
    public void LoadStarter()
    {
        lock (_gate)
        {
            _byStatId = new Dictionary<string, double>(StarterWeights.ByStatId, StringComparer.OrdinalIgnoreCase);
            _normById = new Dictionary<string, double>(StarterWeights.NormById, StringComparer.OrdinalIgnoreCase);
            _target = StarterWeights.Target > 0 ? StarterWeights.Target : 100;
            _threshold = Math.Clamp(StarterWeights.GodRollThreshold, 0, 100);
            Save();
        }
    }
```

In the constructor, after `Load();`, seed the starter set ONLY when no file existed (so an existing user file is never overwritten):

```csharp
    public GearWeightStore(string filePath)
    {
        _filePath = filePath;
        var existed = File.Exists(_filePath);
        Load();
        if (!existed) LoadStarter();   // fresh install → ship meta starter weights
    }
```

(Add `using System.IO;` if not present; `using POE2Radar.Core.Game;` for `StarterWeights`.)

- [ ] **Step 3: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS.

- [ ] **Step 4: Manual verify (no live game needed)**

Delete any `config/stat_weights.json` next to the built exe, run the overlay once, and confirm a fresh `stat_weights.json` is written containing the starter `byStatId` + `normById`. Confirm re-running does not change a user-edited file.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/GearWeightStore.cs
git commit -m "feat(overlay): gear weight store carries norms, seeds meta starter weights, LoadStarter()"
```

---

### Task 9: API — expose `statIds` in `/api/gear`; `reset`/`norm` in `/api/gear-weights`

**Files:**
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`GearJson` ~555)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (`ApplyGearWeights` ~883-900)

**Interfaces:**
- Consumes: `AffixContribution.StatIds` (Task 4); `GearWeightStore.LoadStarter`/`SetNorm` (Task 8).
- Produces: `/api/gear` affixes carry `statIds`; `/api/gear-weights` POST accepts `{reset:"starter"}` and `{setWeight:{...,norm?}}` / `{setNorm:{statId,norm}}`.

- [ ] **Step 1: Expose stat ids in `GearJson`**

In `RadarApp.cs` `GearJson` (~line 555), change the affix projection:

```csharp
                affixes = i.Affixes.Select(a => new { line = a.Line, statIds = a.StatIds, value = a.Value, weight = a.Weight, points = Math.Round(a.Points, 2) }),
```

(`i.Affixes` is `IReadOnlyList<AffixContribution>`; `a.StatIds` now exists from Task 4. The WorldTick scoring loop at ~1018-1024 already builds `Affix(line, ids, val)` and passes through `GearScorer.Score`, so stat ids flow without further change.)

- [ ] **Step 2: Extend `ApplyGearWeights` for `reset`, `setNorm`, and an optional `norm` on `setWeight`**

In `ApiServer.cs` `ApplyGearWeights` (~883-900), wrap the body in a try/catch (the existing method has none — mirror the atlas handlers) and add the new branches:

```csharp
    private void ApplyGearWeights(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("reset", out var rv) && rv.GetString() == "starter")
                _gearWeights.LoadStarter();

            if (root.TryGetProperty("setWeight", out var sw) && sw.ValueKind == JsonValueKind.Object
                && sw.TryGetProperty("statId", out var sid) && sid.GetString() is { Length: > 0 } statId
                && sw.TryGetProperty("weight", out var wv) && wv.TryGetDouble(out var weight))
            {
                _gearWeights.SetWeight(statId, weight);
                if (sw.TryGetProperty("norm", out var nv) && nv.TryGetDouble(out var norm))
                    _gearWeights.SetNorm(statId, norm);
            }

            if (root.TryGetProperty("setNorm", out var sn) && sn.ValueKind == JsonValueKind.Object
                && sn.TryGetProperty("statId", out var nsid) && nsid.GetString() is { Length: > 0 } normStatId
                && sn.TryGetProperty("norm", out var nnv) && nnv.TryGetDouble(out var normVal))
                _gearWeights.SetNorm(normStatId, normVal);

            if (root.TryGetProperty("target", out var tv) && tv.TryGetDouble(out var target))
                _gearWeights.SetTarget(target);
            if (root.TryGetProperty("threshold", out var thv) && thv.TryGetDouble(out var threshold))
                _gearWeights.SetThreshold(threshold);
        }
        catch (JsonException) { /* malformed body → ignore, like the atlas handlers */ }
    }
```

- [ ] **Step 3: Build + gate**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS.

- [ ] **Step 4: Manual verify**

With the scorer on (`enableGearScorer`) and an inventory open: `GET /api/gear` shows each affix with a `statIds` array and nonzero `score`/`points` for life/resist items. `POST /api/gear-weights {"reset":"starter"}` returns `{ok:true, weights:{byStatId,normById,...}}`.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs
git commit -m "feat(api): expose affix statIds in /api/gear; reset/setNorm in /api/gear-weights"
```

---

### Task 10: Dashboard Gear tab — rarity colors, roomier cards, one-click stat-id chips, "Load meta starter weights"

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (gear section ~658-678; gear JS ~1320-1357)

**Interfaces:** consumes `/api/gear` (`affixes[].statIds`) and `/api/gear-weights` (`byStatId`,`normById`,`reset`,`setWeight.norm`).

- [ ] **Step 1: Add a "Load meta starter weights" button + a hint in the Weights card**

In the gear `data-view="gear"` Weights card (the `.row` with `#gStatId`/`#gWeight`/`#gSetWeight`), add a button after `#gSetWeight`:

```html
              <button class="numin" id="gLoadStarter" title="replace your weights with the ladder-meta starter set">Load meta starter</button>
```

- [ ] **Step 2: Render affix stat ids as one-click chips + color items by rarity**

Replace `renderGearItems` (DashboardHtml.cs ~1332-1340) with a version that (a) colors the item name with the existing `.rar-<Rarity>` class, (b) renders each affix's `statIds` as clickable chips that one-click-weight on the meta scale, and (c) shows the rolled value:

```javascript
function renderGearItems(items){
  const el=$('#gItems'); if(!el) return;
  el.innerHTML = items.length ? items.map(it=>{
    const aff=(it.affixes||[]).map(a=>{
      const chips=(a.statIds||[]).map(id=>'<button class="chip g-chip" data-id="'+esc(id)+'" data-val="'+a.value+'" title="weight this stat (meta scale)">'+esc(id)+'</button>').join(' ');
      return '<div class="rl hint-row" style="padding-left:12px">'+esc(a.line||'')+' &middot; roll '+a.value
        +(a.weight?(' &middot; w'+a.weight+' &rarr; '+a.points+'pts'):'')+'<div style="margin-top:3px">'+chips+'</div></div>';
    }).join('');
    return '<div class="row" style="flex-wrap:wrap"><div class="rl"><span class="rar-'+esc(it.rarity||'Normal')+'">'+(it.godRoll?'&#9733; ':'')+esc(it.name||'(item)')+'</span><small>'+esc(it.rarity||'')+' &middot; inv '+it.inventoryId+'</small></div>'
      +'<div class="numin" style="min-width:54px;text-align:right;font-weight:600">'+it.score+'</div>'
      +'<div style="flex-basis:100%">'+aff+'</div></div>';
  }).join('') : '<div class="row"><div class="rl hint-row">No scored items yet. (Turn the scorer on in Settings and open your inventory in-game.)</div></div>';
  // one-click: weight the chip's stat id on the meta scale (10), norm from the observed roll if unknown.
  el.querySelectorAll('.g-chip').forEach(b=>b.onclick=()=>{
    const id=b.dataset.id, val=parseFloat(b.dataset.val)||1;
    const body={setWeight:{statId:id,weight:10}};
    if(!(gWeights.normById&&gWeights.normById[id]>0)) body.setWeight.norm=Math.max(val,1);
    postGear(body);
  });
}
```

- [ ] **Step 3: Add a small chip style (reuse existing tokens)**

In the dashboard `<style>` block (near `.delbtn`, ~line 214), add:

```css
  .chip{font-family:inherit; font-size:10px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:10px; padding:2px 8px; margin:0 4px 4px 0; cursor:pointer}
  .chip:hover{border-color:var(--gold-deep); color:var(--gold)}
```

- [ ] **Step 4: Wire the "Load meta starter" button**

Add next to the existing gear handlers (~1355):

```javascript
$('#gLoadStarter')?.addEventListener('click',()=>{ if(confirm('Replace your weights with the ladder-meta starter set?')) postGear({reset:'starter'}).then(loadGear); });
```

- [ ] **Step 5: Build + gate + manual verify**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS.
Manual: Gear tab shows items with rarity-colored names; each affix shows clickable stat-id chips; clicking a chip adds a weight and the item's score updates on the next refresh; "Load meta starter" repopulates the weights list.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): gear rarity colors + one-click stat-id chips + Load-meta-starter button"
```

---

### Task 11: Dashboard Gear tab — flat score heatmap grid

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (gear section + JS)

**Decision (grounded):** `Poe2Live.ReadInventory` returns a FLAT `InventoryItem(Name, Rarity, Identified, InventoryId, Affixes)` — no slot index or box dims are surfaced (they're read then discarded in `ProbeItemListVecForInventory`). Per the spec's rule, the grid is therefore a **flat score-sorted heatmap grouped by inventory**, not a positional bag replica. (Positional would require threading cell index + box dims out of the drift-prone inventory reader; deferred — and for a dashboard a sorted heatmap is the more useful view anyway.)

**Interfaces:** consumes `/api/gear` items (`name`,`rarity`,`inventoryId`,`score`,`godRoll`).

- [ ] **Step 1: Add a List/Grid view toggle + a grid container to the Items card**

In the gear Items card (`#gItems`), add a small toggle + grid host above `#gItems`:

```html
            <div class="row"><div class="rl">View</div>
              <label class="sw" style="gap:8px"><span style="font-size:11px;color:var(--ink-faint)">List</span>
              <input type="checkbox" id="gGridToggle"><span class="track"></span><span class="knob"></span>
              <span style="font-size:11px;color:var(--ink-faint)">Grid</span></label></div>
            <div id="gGrid" class="gear-grid" style="display:none"></div>
```

- [ ] **Step 2: Add grid CSS (green→red heatmap cells)**

In the `<style>` block add:

```css
  .gear-grid{display:flex; flex-wrap:wrap; gap:6px; padding:8px 0}
  .gcell{width:52px; height:52px; border-radius:3px; border:1px solid var(--line); display:flex; align-items:center; justify-content:center;
         font-size:13px; font-weight:700; color:#0c0a07; cursor:default; overflow:hidden}
  .gcell small{display:block}
```

- [ ] **Step 3: Render the heatmap from the scored items**

Add a `renderGearGrid` + the toggle wiring, and call it from `loadGear`. The cell color ramps green (high) → red (low) by score; the cell title shows name + affixes; cells are grouped by inventory and sorted by score:

```javascript
function scoreColor(s){ // 0=red -> 100=green
  const t=Math.max(0,Math.min(100,s))/100; const h=Math.round(t*120); // 0=red,120=green
  return 'hsl('+h+',60%,55%)';
}
function renderGearGrid(items){
  const el=$('#gGrid'); if(!el) return;
  const sorted=(items||[]).slice().sort((a,b)=>b.score-a.score);
  el.innerHTML = sorted.length ? sorted.map(it=>{
    const t=esc((it.godRoll?'★ ':'')+(it.name||'(item)')+' — '+it.score+' ('+(it.rarity||'')+')');
    return '<div class="gcell" style="background:'+scoreColor(it.score)+'" title="'+t+'">'+it.score+'</div>';
  }).join('') : '<div class="rl hint-row">No scored items yet.</div>';
}
$('#gGridToggle')?.addEventListener('change',e=>{
  const grid=e.target.checked; $('#gGrid').style.display=grid?'flex':'none'; $('#gItems').style.display=grid?'none':'block';
});
```

In `loadGear`, after `renderGearItems(g.items||[]);` add `renderGearGrid(g.items||[]);`.

- [ ] **Step 4: Build + gate + manual verify**

Run: `dotnet build POE2Radar.slnx` → 0W/0E. Gate → PASS.
Manual: toggle List→Grid; items appear as score-colored cells (green=high), highest first; hovering a cell shows name + score; toggling back shows the list.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(dashboard): gear flat score heatmap grid (green->red), list/grid toggle"
```

---

### Task 12: Release v0.1.9 — version bump, checklist, full verification

**Files:**
- Modify: the version source (search: `git grep -n "0.1.8"` — typically `UpdateChecker.cs` `Current` const and/or the csproj `<Version>`).
- Modify: `docs/release-checklist.md` (add the three new manual checks).

- [ ] **Step 1: Bump the version to 0.1.9**

Run: `git grep -n "0\.1\.8"` → update the `Current`/`<Version>` to `0.1.9` (do NOT touch unrelated matches like offsets).

- [ ] **Step 2: Add manual live checks to `docs/release-checklist.md`**

Append under the manual section:
- Menu chord: L3+R3 toggles the top-left nav list open/closed without changing the active target; Ctrl+Alt+M does the same; clicking the chip still toggles; the chip reads "POE2GPS".
- Monolith panel: off by default; the Settings "Monolith reward panel" toggle shows/hides it live.
- Gear v2: with the scorer on, items show nonzero scores out of the box; each affix shows clickable stat-id chips; "Load meta starter" repopulates weights; rarity colors + the grid heatmap render.

- [ ] **Step 3: Full verification sweep**

Run, expecting all green:
```bash
dotnet build POE2Radar.slnx -c Release        # 0 warnings, 0 errors
dotnet test  POE2Radar.slnx                    # all pass (incl. the 4 new test files)
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1          # PASS
powershell -ExecutionPolicy Bypass -File scripts/scrub-strings.ps1 -SelfTest  # PASSED
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "release: v0.1.9 — nav-menu chord, monolith panel default-off, Gear Scorer v2 (meta weights)"
```

(Tagging/publishing happens via the finishing-a-development-branch flow + the `v*` release workflow — not in this plan.)

---

## Self-Review (author checklist — completed)

- **Spec coverage:** Part 1 → Tasks 1-2; Part 2 → Task 3; Part 3 (3a statIds → Task 9; 3b meta weights + scorer norm → Tasks 4-8; 3c one-click chips → Task 10; 3d colors → Task 10; 3e grid → Task 11). Branding fold-in → Task 2. Provenance/licensing honored (Tincture vendored, no PoB Lua). ✓
- **Grid decision** made explicit (flat, with the grounded reason) per the spec's positional-vs-flat rule. ✓
- **Type consistency:** `AffixContribution(Line, StatIds, Value, Weight, Points)` and `StatWeights(..., NormById)` defined in Task 4 and consumed identically in Tasks 8-10; `ControllerInput(Cycle, MenuToggle)` defined in Task 1, consumed in Task 2; `StarterWeights.ByStatId/NormById` defined in Task 7, consumed in Task 8. ✓
- **No placeholders:** every code step carries real code; the one runtime-discovered value (which file holds the version string) is resolved with an exact `git grep` in Task 12. ✓
- **Compliance:** no input-emission/process-write/pricing APIs introduced; all new input is read-only + foreground-gated; gate run at the end of every code task. ✓
