# Target Cycle Order Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement
> this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. The pure helper (Task 1) is TDD with
> xUnit; the RadarApp/UI tasks have NO unit-testable surface (live input/overlay) — their cycle is
> **build + compliance gate**.

**Goal:** Make the Quick-Target Cycler follow the radar-menu order by default, with the priority/distance
"intelligent" ranking as an opt-in toggle, plus hold-to-fast-cycle on controller and keyboard.

**Architecture:** A new pure `Core/Input/HoldRepeat.cs` (tap→hold-repeat state machine, unit-tested) is
fed the currently-held cycle direction from both inputs. RadarApp picks the cycle list from `_navTargets`
(menu order, default) or `_rankedTargets` (priority/distance) based on a new `IntelligentTargetCycling`
setting. `TargetCycler` and the nav-menu display are unchanged.

**Tech Stack:** C# / .NET 10 (net10.0-windows, x64), `POE2Radar.slnx`, xUnit (tests/POE2Radar.Tests),
vanilla-JS dashboard embedded in `DashboardHtml.cs`. Branch `feat/cycle-order-toggle`.

## Global Constraints

- **Read-only / compliance:** reads keyboard + XInput STATE only; never `SendInput`/`PostMessage`; no
  process writes; no pricing. `scripts/compliance-gate.ps1` + `scripts/scrub-strings.ps1 -SelfTest` PASS.
- **Exact defaults:** `IntelligentTargetCycling = false`; `CycleHoldDelayMs = 400`; `CycleHoldIntervalMs = 150`.
- **Unchanged:** `EnableTargetHotkeys`/`EnableControllerCycle` stay `true`; the nav-menu display; routes;
  slot hotkeys (Ctrl+Alt+1–9 pick the Nth of the *active* list); the L3+R3 / Ctrl+Alt+M menu toggle.
- Build clean: `dotnet build POE2Radar.slnx -c Release` → 0 warnings, 0 errors (TreatWarningsAsErrors).

---

### Task 1: `Core/Input/HoldRepeat.cs` — pure tap/hold-repeat helper (TDD)

**Files:**
- Create: `src/POE2Radar.Core/Input/HoldRepeat.cs`
- Test: `tests/POE2Radar.Tests/HoldRepeatTests.cs`

**Interfaces:**
- Produces: `HoldRepeat(TimeSpan initialDelay, TimeSpan interval)` ctor; `int Update(int heldDir, DateTime now)`
  — returns steps to fire this poll (0 or 1). `heldDir`: -1 prev, +1 next, 0 none.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using POE2Radar.Core.Input;
using Xunit;

namespace POE2Radar.Tests;

public class HoldRepeatTests
{
    private static HoldRepeat New() => new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(150));
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int ms) => T0.AddMilliseconds(ms);

    [Fact] public void Tap_fires_one_step_on_press()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));   // press → one step
        Assert.Equal(0, h.Update(0, At(10)));   // release → nothing
    }

    [Fact] public void Hold_within_initial_delay_does_not_repeat()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(0, h.Update(+1, At(100)));  // 100ms < 400ms delay
        Assert.Equal(0, h.Update(+1, At(399)));
    }

    [Fact] public void Hold_past_delay_repeats_at_interval()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));    // tap
        Assert.Equal(1, h.Update(+1, At(400)));  // first repeat (>= delay, >= interval since press)
        Assert.Equal(0, h.Update(+1, At(500)));  // 100ms since last fire < 150
        Assert.Equal(1, h.Update(+1, At(550)));  // 150ms since last fire → repeat
    }

    [Fact] public void Direction_flip_retaps_immediately()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(1, h.Update(-1, At(50)));   // changed direction → immediate tap
    }

    [Fact] public void Release_resets_so_next_press_taps()
    {
        var h = New();
        Assert.Equal(1, h.Update(+1, At(0)));
        Assert.Equal(0, h.Update(0, At(10)));
        Assert.Equal(1, h.Update(+1, At(20)));   // fresh press taps again
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test POE2Radar.slnx -c Release --filter HoldRepeatTests`
Expected: FAIL — `HoldRepeat` does not exist.

- [ ] **Step 3: Implement `HoldRepeat`**

```csharp
namespace POE2Radar.Core.Input;

/// <summary>
/// Tap-and-hold-repeat state machine for cycle inputs. Pure + clock-injected (every method takes the
/// current time) so it is fully unit-testable. A tap (the held direction changing from 0 to ±1) fires
/// one step immediately; holding the same direction past <c>initialDelay</c> repeats one step every
/// <c>interval</c>. Releasing (heldDir 0) resets. No game/UI/input-send dependency.
/// </summary>
public sealed class HoldRepeat
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private int _dir;               // direction currently held (0 none, -1 prev, +1 next)
    private DateTime _holdStart;
    private DateTime _lastFire;

    public HoldRepeat(TimeSpan initialDelay, TimeSpan interval)
    {
        _initialDelay = initialDelay;
        _interval = interval;
    }

    /// <summary>Returns the number of cycle steps to fire this poll (0 or 1). <paramref name="heldDir"/>:
    /// -1 = prev held, +1 = next held, 0 = nothing held.</summary>
    public int Update(int heldDir, DateTime now)
    {
        if (heldDir == 0) { _dir = 0; return 0; }            // released → reset
        if (heldDir != _dir)                                  // fresh press / direction flip → tap
        {
            _dir = heldDir;
            _holdStart = now;
            _lastFire = now;
            return 1;
        }
        if (now - _holdStart < _initialDelay) return 0;       // still in the tap window
        if (now - _lastFire >= _interval) { _lastFire = now; return 1; }
        return 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test POE2Radar.slnx -c Release --filter HoldRepeatTests`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Input/HoldRepeat.cs tests/POE2Radar.Tests/HoldRepeatTests.cs
git commit -m "feat(input): HoldRepeat pure tap/hold-repeat helper (TDD)"
```

---

### Task 2: Setting + cycle-source swap (menu order by default)

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs` (after the `EnableControllerCycle` line ~44)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`_rankedTargets` build ~1151; `Cycle` ~1547; `CycleToIndex` ~1562; add `ActiveCycleList`)

**Interfaces:**
- Consumes: `RankedTarget(string Id, string Label/Name, string Category)` (existing record), `_navTargets`
  (`List<NavTarget>`; `NavTarget` has `.Id`, `.Name`), `_rankedTargets`.
- Produces: `RadarSettings.IntelligentTargetCycling` (bool), `.CycleHoldDelayMs` (int), `.CycleHoldIntervalMs`
  (int); `RadarApp.ActiveCycleList() : IReadOnlyList<RankedTarget>`.

- [ ] **Step 1: Add the settings.** In `RadarSettings.cs`, immediately after the `EnableControllerCycle`
  property (line ~44), add:

```csharp
    // Quick-Target Cycler ORDER: false (default) = cycle follows the radar-menu order (the nav dropdown:
    // landmarks/tiles, then nearest entities). true = priority-then-distance "intelligent" ranking.
    public bool IntelligentTargetCycling { get; set; } = false;
    // Hold-to-fast-cycle timing (controller L3/R3 + keyboard Ctrl+Alt+ [ / ]): tap = one step; hold past
    // CycleHoldDelayMs auto-repeats one step every CycleHoldIntervalMs.
    public int CycleHoldDelayMs { get; set; } = 400;
    public int CycleHoldIntervalMs { get; set; } = 150;
```

- [ ] **Step 2: Add the `ActiveCycleList` helper.** In `RadarApp.cs`, immediately above `BuildRankedTargets`
  (line ~1530), add:

```csharp
    /// <summary>The id-ordered list the cycler steps through: the radar-MENU order (_navTargets) by
    /// default, or the priority/distance ranking when IntelligentTargetCycling is on. Render thread;
    /// reads the volatile published lists.</summary>
    private IReadOnlyList<RankedTarget> ActiveCycleList()
    {
        if (_settings.IntelligentTargetCycling) return _rankedTargets;
        var nav = _navTargets;
        var list = new List<RankedTarget>(nav.Count);
        foreach (var t in nav) list.Add(new RankedTarget(t.Id, t.Name, ""));
        return list;
    }
```

- [ ] **Step 3: Point `Cycle` and `CycleToIndex` at the active list.** In `RadarApp.cs`, change the first
  line of each method body from `var ranked = _rankedTargets;` to `var ranked = ActiveCycleList();`:

`Cycle` (line ~1549):
```csharp
    private void Cycle(CycleAction action)
    {
        var ranked = ActiveCycleList();
        var ids = new List<string>(ranked.Count);
        foreach (var r in ranked) ids.Add(r.Id);
        var next = action switch
        {
            CycleAction.Next => TargetCycler.Next(ids, _activeTargetId),
            CycleAction.Prev => TargetCycler.Prev(ids, _activeTargetId),
            _                => null,   // Clear
        };
        ApplyActive(next, ranked);
    }
```

`CycleToIndex` (line ~1562):
```csharp
    private void CycleToIndex(int oneBased)
    {
        var ranked = ActiveCycleList();
        var ids = new List<string>(ranked.Count);
        foreach (var r in ranked) ids.Add(r.Id);
        ApplyActive(TargetCycler.AtIndex(ids, oneBased), ranked);
    }
```

- [ ] **Step 4: Only build `_rankedTargets` when intelligent + a cycler is enabled.** In `RadarApp.cs`
  (line ~1151), change:

```csharp
        // Priority/distance ranking is only needed for INTELLIGENT cycling; default cycling uses _navTargets.
        _rankedTargets = (_settings.IntelligentTargetCycling
                          && (_settings.EnableTargetHotkeys || _settings.EnableControllerCycle))
            ? BuildRankedTargets(player) : System.Array.Empty<RankedTarget>();
```

- [ ] **Step 5: Build + gate.**

Run: `dotnet build POE2Radar.slnx -c Release` → `0 Warning(s) 0 Error(s)`.
Run: `powershell -NoProfile -File scripts/compliance-gate.ps1` → `PASS`.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(cycle): default to radar-menu order; IntelligentTargetCycling opt-in"
```

---

### Task 3: Hold-to-fast wiring (controller held-dir + two HoldRepeats)

**Files:**
- Modify: `src/POE2Radar.Overlay/Input/ControllerCycler.cs` (the `Poll` method)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (fields near `_controllerCycler`; ctor; keyboard cycle
  handler ~1365–1383; controller poll ~1407–1415)

**Interfaces:**
- Consumes: `HoldRepeat` (Task 1); `ControllerChord.LeftThumb`/`.RightThumb`; `Cycle(CycleAction)`,
  `CycleToIndex(int)` (Task 2); `_settings.CycleHoldDelayMs`/`.CycleHoldIntervalMs`.
- Produces: `ControllerCycler.Poll() : (ControllerInput input, int heldDir)`.

- [ ] **Step 1: Make `ControllerCycler.Poll` also report the held direction.** Replace the body of
  `src/POE2Radar.Overlay/Input/ControllerCycler.cs` `Poll()` and add a helper:

```csharp
    /// <summary>Poll once. Returns the resolved menu-toggle/cycle edge AND the currently-HELD cycle
    /// direction (L3 down = -1, R3 down = +1, both down = 0 (menu mode), none = 0). Always call each
    /// frame so the edge state stays correct.</summary>
    public (ControllerInput input, int heldDir) Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return (default, 0); }
        var result = ControllerChord.Resolve(_prev, cur);
        _prev = cur;
        return (result, HeldDir(cur));
    }

    private static int HeldDir(ushort cur)
    {
        var l = (cur & ControllerChord.LeftThumb) != 0;
        var r = (cur & ControllerChord.RightThumb) != 0;
        if (l && r) return 0;          // both held = menu mode; suppress cycle
        return l ? -1 : r ? +1 : 0;
    }
```

- [ ] **Step 2: Add the two `HoldRepeat` fields + construct them.** In `RadarApp.cs`, near the
  `_controllerCycler` field declaration, add (add `using POE2Radar.Core.Input;` at the top if not present):

```csharp
    private readonly HoldRepeat _controllerHold;
    private readonly HoldRepeat _keyboardHold;
```

  In the `RadarApp` constructor, after `_settings` is assigned, construct them:

```csharp
        _controllerHold = new HoldRepeat(TimeSpan.FromMilliseconds(_settings.CycleHoldDelayMs),
                                         TimeSpan.FromMilliseconds(_settings.CycleHoldIntervalMs));
        _keyboardHold   = new HoldRepeat(TimeSpan.FromMilliseconds(_settings.CycleHoldDelayMs),
                                         TimeSpan.FromMilliseconds(_settings.CycleHoldIntervalMs));
```

- [ ] **Step 3: Rewrite the keyboard cycle handler.** Replace the block at `RadarApp.cs` lines ~1365–1383
  with (the `[`/`]` cycle now uses HoldRepeat; `0` clear + `1–9` slot keep the discrete debounce):

```csharp
        // Quick-Target Cycler (keyboard): Ctrl+Alt+ ] next / [ prev (hold-to-fast via HoldRepeat),
        // 1-9 jump-to-slot, 0 clear (discrete, debounced). Foreground-gated. Reads keys only — sends
        // nothing to the game. Cycle keys are [ ] (0xDB/0xDD), NOT arrows (Ctrl+Alt+Arrow rotates Intel).
        if (_settings.EnableTargetHotkeys)
        {
            var foreground = _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd;
            var ctrlAlt = foreground && Down(0x11) && Down(0x12);
            var kbDir = ctrlAlt ? (Down(0xDD) ? +1 : Down(0xDB) ? -1 : 0) : 0;   // ] = +1, [ = -1
            var steps = _keyboardHold.Update(kbDir, DateTime.UtcNow);
            for (var i = 0; i < steps; i++) Cycle(kbDir < 0 ? CycleAction.Prev : CycleAction.Next);

            if (ctrlAlt && DateTime.UtcNow >= _nextCycleAt)
            {
                var fired = false;
                if (Down(0x30)) { Cycle(CycleAction.Clear); fired = true; }   // 0 = clear
                else for (var n = 1; n <= 9; n++)
                    if (Down(0x30 + n)) { CycleToIndex(n); fired = true; break; }   // 1..9
                if (fired) _nextCycleAt = DateTime.UtcNow.AddMilliseconds(250);
            }
        }
```

- [ ] **Step 4: Rewrite the controller poll.** Replace the block at `RadarApp.cs` lines ~1407–1415 with:

```csharp
        if (_settings.EnableControllerCycle)
        {
            var foreground = _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd;
            var (input, heldDir) = _controllerCycler.Poll();   // always poll to keep edge state fresh
            if (foreground && input.MenuToggle) _navMenuExpanded = !_navMenuExpanded;
            var steps = _controllerHold.Update(foreground ? heldDir : 0, DateTime.UtcNow);
            if (foreground) for (var i = 0; i < steps; i++)
                Cycle(heldDir < 0 ? CycleAction.Prev : CycleAction.Next);
        }
```

- [ ] **Step 5: Build + gate.**

Run: `dotnet build POE2Radar.slnx -c Release` → `0 Warning(s) 0 Error(s)`.
Run: `powershell -NoProfile -File scripts/compliance-gate.ps1` → `PASS`.

- [ ] **Step 6: Commit**

```bash
git add src/POE2Radar.Overlay/Input/ControllerCycler.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(cycle): hold-to-fast-cycle on controller + keyboard via HoldRepeat"
```

---

### Task 4: Settings toggle (API round-trip + dashboard, at the top)

**Files:**
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (GET serialize ~656; POST apply ~713)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (settings card, above the "Target hotkeys" row ~564)

**Interfaces:**
- Consumes: `_settings.IntelligentTargetCycling` (Task 2); `TryBool` (existing).

- [ ] **Step 1: Serialize it in the settings GET.** In `ApiServer.cs`, after the `enableControllerCycle`
  line (~656), add:

```csharp
        intelligentTargetCycling = _settings.IntelligentTargetCycling,
```

- [ ] **Step 2: Apply it in the settings POST.** In `ApiServer.cs`, after the `enableControllerCycle`
  case (~713), add:

```csharp
                case "intelligentTargetCycling" when TryBool(p.Value, out var b): _settings.IntelligentTargetCycling = b; applied.Add(p.Name); break;
```

- [ ] **Step 3: Add the toggle at the TOP of the settings card.** In `DashboardHtml.cs`, immediately
  BEFORE the "Target hotkeys" row (the `<div class="row">` at ~564), insert:

```csharp
            <div class="row"><div class="rl">Intelligent target cycling<small>On = smart priority/distance order &mdash; Off (default) = cycle follows the radar menu (nav dropdown order)</small></div>
              <label class="sw"><input type="checkbox" data-set="intelligentTargetCycling"><span class="track"></span><span class="knob"></span></label></div>
```

  (The generic `data-set` checkbox wiring in `loadSettings`/the change handler round-trips it like every
  other toggle — no extra JS needed.)

- [ ] **Step 4: Build + gate.**

Run: `dotnet build POE2Radar.slnx -c Release` → `0 Warning(s) 0 Error(s)`.
Run: `powershell -NoProfile -File scripts/compliance-gate.ps1` → `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(cycle): Intelligent target cycling toggle at top of Settings"
```

---

### Task 5: Integration sweep + final review

- [ ] **Step 1: Full build + both gates + the HoldRepeat tests.**

Run: `dotnet build POE2Radar.slnx -c Release` → 0/0.
Run: `dotnet test POE2Radar.slnx -c Release` → all pass (incl. 5 HoldRepeat).
Run: `powershell -NoProfile -File scripts/compliance-gate.ps1` → PASS.
Run: `powershell -NoProfile -File scripts/scrub-strings.ps1 -SelfTest` → PASSED.

- [ ] **Step 2: Verify the integration seams** (read-only inspection — no code change unless a seam fails):
  1. Toggle OFF (default): `ActiveCycleList()` returns `_navTargets` order; `_rankedTargets` not built.
  2. Toggle ON: `ActiveCycleList()` returns `_rankedTargets`; `BuildRankedTargets` runs.
  3. Controller: tap L3/R3 steps once; hold past 400ms repeats ~150ms; L3+R3 still toggles the menu.
  4. Keyboard: tap/hold `[`/`]` cycles; `0`/`1–9` still work; Ctrl+Alt+M still toggles the menu.
  5. `/api/settings` GET returns `intelligentTargetCycling`; POST applies + persists; dashboard toggle shows it.

- [ ] **Step 3: Final whole-branch review** (controller dispatches the code-reviewer subagent over
  `git merge-base main HEAD`..HEAD).
