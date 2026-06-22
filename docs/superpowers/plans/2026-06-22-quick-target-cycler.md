# Quick-Target Cycler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hotkeys (`Ctrl+Alt+[`/`]`/`1-9`/`0`) and controller stick-clicks (L3 prev / R3 next) that quickly switch the radar's single active nav target up/down a priority-then-distance ranked list — manual target control while the Director matures.

**Architecture:** A pure `TargetCycler` core (Next/Prev/AtIndex over a ranked id list, id-stable) drives a single "active target" that replaces `_selectedIds` (reusing the existing selection→route pipeline). The ranked list is built **world-side** by reusing `CampaignObjectives.Rank` (covered content, priority+distance) and appending uncatalogued targets by distance, published lock-free. Two read-only input drivers (keyboard chords via the existing `GetAsyncKeyState`, controller via a new read-only `XInputGetState` poll) feed the same core on the render thread, foreground-gated. A brief on-screen indicator shows the active target.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`, x64), `LibraryImport` P/Invoke, xUnit. Solution `POE2Radar.slnx`.

## Global Constraints

- **Read-only.** Reads key/controller state to drive the overlay's own selection. NO input emission (`SendInput`), NO process writes. `scripts/compliance-gate.ps1` must print `PASS` (`XInputGetState`/`GetAsyncKeyState` are reads, not forbidden symbols).
- `TreatWarningsAsErrors=true`, `Nullable=enable` — every build is **0 Warning(s), 0 Error(s)**.
- **Foreground-gated.** Hotkeys/controller only act while PoE2 is the focused window: `_gameHwnd != 0 && GetForegroundWindow() == _gameHwnd`.
- **Single active target (replace).** A cycle/select sets `_selectedIds` to exactly one id (under `_navLock`). F6 multi-select still works.
- **Reuse the ranking.** Order = `CampaignObjectives.Rank` (priority desc, distance asc) for covered content, then uncatalogued targets by distance. Do not write a new priority engine.
- **Cycle keys are `[`/`]` (VK 0xDB/0xDD), NOT arrows** — `Ctrl+Alt+Arrow` is the Intel-graphics screen-rotation shortcut and would flip the user's display.
- **Controller:** L3 (`0x0040`) = prev, R3 (`0x0080`) = next. Bare clicks (both are combat-dead in PoE2).
- Pure logic in Core (`tests/POE2Radar.Tests` references `POE2Radar.Core` only). Overlay-layer drivers are build/gate-verified; the live controller feel is a manual release-checklist item.

---

## File Structure

- `src/POE2Radar.Core/Navigation/TargetCycler.cs` — **NEW.** Pure cycle/select core. Unit-tested.
- `src/POE2Radar.Overlay/Input/XInputNative.cs` — **NEW.** Read-only `XInputGetState` P/Invoke + structs.
- `src/POE2Radar.Overlay/Input/ControllerCycler.cs` — **NEW.** Edge-detect L3/R3 → -1/0/+1.
- `src/POE2Radar.Overlay/RadarApp.cs` — **MODIFY.** Publish `_rankedTargets`; `Cycle`/`CycleToIndex`/`SetActiveTarget`/`_activeTargetId`; the keyboard + controller blocks in `HandleHotkeys`; the cycle-indicator state.
- `src/POE2Radar.Overlay/Overlay/RenderContext.cs` — **MODIFY.** `RankedTarget` + `CycleIndicator` records + a `CycleIndicator?` context field.
- `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` — **MODIFY.** Draw the indicator.
- `src/POE2Radar.Overlay/Config/RadarSettings.cs` — **MODIFY.** `EnableTargetHotkeys` + `EnableControllerCycle` + round-trip.
- `src/POE2Radar.Overlay/Web/ApiServer.cs` + `Web/DashboardHtml.cs` — **MODIFY.** Settings round-trip + toggles.
- `docs/upstream-merge.md`, `docs/release-checklist.md` — **MODIFY.**
- `tests/POE2Radar.Tests/TargetCyclerTests.cs` — **NEW.**

---

## Task 1: TargetCycler core (Core, pure, TDD)

**Files:**
- Create: `src/POE2Radar.Core/Navigation/TargetCycler.cs`
- Test: `tests/POE2Radar.Tests/TargetCyclerTests.cs`

**Interfaces:**
- Produces: `static string? TargetCycler.Next(IReadOnlyList<string> ranked, string? current)`, `Prev(...)`, `static string? AtIndex(IReadOnlyList<string> ranked, int oneBased)`.

- [ ] **Step 1: Write the failing test**

Create `tests/POE2Radar.Tests/TargetCyclerTests.cs`:

```csharp
using POE2Radar.Core.Navigation;

public class TargetCyclerTests
{
    private static readonly string[] L = { "a", "b", "c" };

    [Fact] public void Next_Advances() => Assert.Equal("b", TargetCycler.Next(L, "a"));
    [Fact] public void Next_WrapsToFirst() => Assert.Equal("a", TargetCycler.Next(L, "c"));
    [Fact] public void Next_NullCurrent_StartsAtTop() => Assert.Equal("a", TargetCycler.Next(L, null));
    [Fact] public void Next_MissingCurrent_StartsAtTop() => Assert.Equal("a", TargetCycler.Next(L, "x"));
    [Fact] public void Next_Empty_Null() => Assert.Null(TargetCycler.Next(System.Array.Empty<string>(), "a"));

    [Fact] public void Prev_Retreats() => Assert.Equal("a", TargetCycler.Prev(L, "b"));
    [Fact] public void Prev_WrapsToLast() => Assert.Equal("c", TargetCycler.Prev(L, "a"));
    [Fact] public void Prev_NullCurrent_StartsAtBottom() => Assert.Equal("c", TargetCycler.Prev(L, null));
    [Fact] public void Prev_MissingCurrent_StartsAtBottom() => Assert.Equal("c", TargetCycler.Prev(L, "x"));

    [Fact] public void AtIndex_OneBased() => Assert.Equal("a", TargetCycler.AtIndex(L, 1));
    [Fact] public void AtIndex_Last() => Assert.Equal("c", TargetCycler.AtIndex(L, 3));
    [Fact] public void AtIndex_OutOfRange_Null() => Assert.Null(TargetCycler.AtIndex(L, 4));
    [Fact] public void AtIndex_Zero_Null() => Assert.Null(TargetCycler.AtIndex(L, 0));

    [Fact] public void Single_NextWrapsToSelf() => Assert.Equal("only", TargetCycler.Next(new[] { "only" }, "only"));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test POE2Radar.slnx`
Expected: FAIL to compile — `TargetCycler` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/POE2Radar.Core/Navigation/TargetCycler.cs`:

```csharp
namespace POE2Radar.Core.Navigation;

/// <summary>
/// Pure cycle/select logic over an already-ranked list of target ids. Tracks the active target BY ID
/// (not index), so it stays correct as the list rebuilds and targets despawn every tick — Next/Prev
/// re-find the current id in the fresh list before moving. No game/UI dependency; fully unit-testable.
/// </summary>
public static class TargetCycler
{
    /// <summary>The id after <paramref name="current"/>, wrapping to the first. If current is null or no
    /// longer present, returns the first id. Null when the list is empty.</summary>
    public static string? Next(IReadOnlyList<string> ranked, string? current)
    {
        if (ranked.Count == 0) return null;
        var i = current is null ? -1 : IndexOf(ranked, current);
        return i < 0 ? ranked[0] : ranked[(i + 1) % ranked.Count];
    }

    /// <summary>The id before <paramref name="current"/>, wrapping to the last. If current is null or no
    /// longer present, returns the last id. Null when the list is empty.</summary>
    public static string? Prev(IReadOnlyList<string> ranked, string? current)
    {
        if (ranked.Count == 0) return null;
        var i = current is null ? -1 : IndexOf(ranked, current);
        return i < 0 ? ranked[^1] : ranked[(i - 1 + ranked.Count) % ranked.Count];
    }

    /// <summary>The id at 1-based slot <paramref name="oneBased"/> (1 = first), or null if out of range.</summary>
    public static string? AtIndex(IReadOnlyList<string> ranked, int oneBased)
    {
        var i = oneBased - 1;
        return (uint)i < (uint)ranked.Count ? ranked[i] : null;
    }

    private static int IndexOf(IReadOnlyList<string> ranked, string id)
    {
        for (var i = 0; i < ranked.Count; i++) if (ranked[i] == id) return i;
        return -1;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test POE2Radar.slnx`
Expected: PASS — existing tests + 14 new `TargetCyclerTests`.

- [ ] **Step 5: Commit**

```bash
git add src/POE2Radar.Core/Navigation/TargetCycler.cs tests/POE2Radar.Tests/TargetCyclerTests.cs
git commit -m "feat(cycler): pure TargetCycler core (Next/Prev/AtIndex, id-stable, tested)"
```

---

## Task 2: Ranked targets + Cycle wiring (RadarApp)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (add records)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (publish ranked list + cycle methods + active id)

**Interfaces:**
- Consumes: `TargetCycler.Next/Prev/AtIndex` (Task 1); existing `CampaignObjectives.Rank(IReadOnlyList<Poe2Live.EntityDot>, IReadOnlyList<Poe2Live.Landmark>, NumVec2) → IReadOnlyList<RankedObjective>` where `RankedObjective(Id, Label, Category, Priority, DistanceSq)`; `_navTargets` (`List<NavTarget>`, ids `"t:<key>"`/`"e:<id>"`); `_selectedIds` + `_navLock`; `_entities`, `_landmarks`, `_campaign`, `_settings`.
- Produces: `_rankedTargets` (volatile `IReadOnlyList<RankedTarget>`); `void Cycle(CycleAction)`; `void CycleToIndex(int)`; `CycleIndicator? _cycleIndicator`.

- [ ] **Step 1: Add the records to `RenderContext.cs`**

In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, just after the `NavTarget` record (line ~14), add:

```csharp
/// <summary>One entry in the priority-then-distance ranked target list the cycler walks.</summary>
public readonly record struct RankedTarget(string Id, string Name, string Category);

/// <summary>Transient on-screen "active target" indicator state (drawn briefly after a cycle).</summary>
public sealed record CycleIndicator(int Pos, int Total, string Name, string Category, System.DateTime Expiry);
```

- [ ] **Step 2: Add the fields to `RadarApp.cs`**

Find `private volatile List<NavTarget> _navTargets = new();` (line ~216) and add directly below it:

```csharp
    private volatile IReadOnlyList<RankedTarget> _rankedTargets = System.Array.Empty<RankedTarget>();
    private string? _activeTargetId;          // the cycler's current single active target (render thread)
    private CycleIndicator? _cycleIndicator;  // transient overlay indicator (render thread)
    private enum CycleAction { Next, Prev, Clear }
```

- [ ] **Step 3: Publish the ranked list in `WorldTick`**

Find `_navTargets = BuildNavTargets(player);` (line ~1095) and add directly below it:

```csharp
        // Publish the priority-then-distance ranked target list for the Quick-Target Cycler (read-only;
        // computed world-side where the catalog + entities live). Only when a cycler input is enabled.
        _rankedTargets = (_settings.EnableTargetHotkeys || _settings.EnableControllerCycle)
            ? BuildRankedTargets(player) : System.Array.Empty<RankedTarget>();
```

- [ ] **Step 4: Add `BuildRankedTargets` + the cycle methods to `RadarApp.cs`**

Add these methods next to `BuildNavTargets` (after its closing brace, ~line 1418):

```csharp
    /// <summary>Rank the current nav targets by catalog priority (desc) then distance (asc): reuse the
    /// Director's Rank for covered content, then append uncatalogued targets by distance. World-thread.</summary>
    private IReadOnlyList<RankedTarget> BuildRankedTargets(NumVec2 player)
    {
        var nav = _navTargets;
        if (nav.Count == 0) return System.Array.Empty<RankedTarget>();
        var result = new List<RankedTarget>(nav.Count);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _campaign.Rank(_entities, _landmarks, player))   // priority desc, distance asc; covered only
            if (covered.Add(r.Id)) result.Add(new RankedTarget(r.Id, r.Label, r.Category));
        foreach (var t in nav.Where(t => !covered.Contains(t.Id))
                             .OrderBy(t => NumVec2.DistanceSquared(t.Grid, player)))
            result.Add(new RankedTarget(t.Id, t.Name, ""));
        return result;
    }

    /// <summary>Apply a cycle action (render thread): pick the new active id and route to it (single-active).</summary>
    private void Cycle(CycleAction action)
    {
        var ranked = _rankedTargets;
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

    /// <summary>Jump to 1-based slot N (render thread).</summary>
    private void CycleToIndex(int oneBased)
    {
        var ranked = _rankedTargets;
        var ids = new List<string>(ranked.Count);
        foreach (var r in ranked) ids.Add(r.Id);
        ApplyActive(TargetCycler.AtIndex(ids, oneBased), ranked);
    }

    private void ApplyActive(string? id, IReadOnlyList<RankedTarget> ranked)
    {
        _activeTargetId = id;
        SetActiveTarget(id);   // single-active: replace _selectedIds with just this one (or none)
        if (id is null) { _cycleIndicator = null; return; }
        var pos = 0;
        for (var i = 0; i < ranked.Count; i++) if (ranked[i].Id == id) { pos = i; break; }
        var rt = ranked[pos];
        _cycleIndicator = new CycleIndicator(pos + 1, ranked.Count, rt.Name, rt.Category, DateTime.UtcNow.AddSeconds(2));
    }

    /// <summary>Single-active selection: clear the nav selection and add this one id (or none). Only edits
    /// _selectedIds under _navLock — trackers/routes reconcile on the tick thread (like ToggleSelectionCore).</summary>
    private void SetActiveTarget(string? id)
    {
        lock (_navLock)
        {
            _selectedIds.Clear();
            if (id is not null) _selectedIds.Add(id);
        }
    }
```

- [ ] **Step 5: Add the `using` for the cycler**

At the top of `RadarApp.cs`, find `using POE2Radar.Core.Game;` and add directly below it:

```csharp
using POE2Radar.Core.Navigation;
```

- [ ] **Step 6: Build + gate + commit**

```bash
dotnet build POE2Radar.slnx
```
Expected: Build succeeded. 0 Warning(s), 0 Error(s).

```bash
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1
```
Expected: `COMPLIANCE GATE: PASS`.

```bash
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/RadarApp.cs
git commit -m "feat(cycler): publish ranked targets + single-active cycle wiring"
```

---

## Task 3: Keyboard driver + setting (RadarApp + settings round-trip)

**Files:**
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (`HandleHotkeys` block + a debounce field)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` (settings GET + ApplySettings)
- Modify: `src/POE2Radar.Overlay/Web/DashboardHtml.cs` (toggle)

**Interfaces:**
- Consumes: `Cycle(CycleAction)`, `CycleToIndex(int)` (Task 2); `Down(int)`, `_gameHwnd`, `GetForegroundWindow()` (existing).
- Produces: `RadarSettings.EnableTargetHotkeys`.

- [ ] **Step 1: Add the setting**

In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, find `public bool EnableGearScorer { get; set; } = false;` and add below it:

```csharp
    // Quick-Target Cycler keyboard hotkeys (Ctrl+Alt+ [ ] / 1-9 / 0) to switch the active radar target.
    // Reads keys to change the overlay's selection — never sends input to the game.
    public bool EnableTargetHotkeys { get; set; } = true;
```

- [ ] **Step 2: Add the debounce field**

In `src/POE2Radar.Overlay/RadarApp.cs`, find `_cycleIndicator` (added in Task 2) and add below it:

```csharp
    private DateTime _nextCycleAt = DateTime.MinValue;
```

- [ ] **Step 3: Add the keyboard block to `HandleHotkeys`**

In `HandleHotkeys` (`src/POE2Radar.Overlay/RadarApp.cs`), find the F10 inspector block's closing — the line `AtlasRoutePick();` and its closing `}` — and add this block right after that `}` (anywhere inside `HandleHotkeys` after the existing keys is fine):

```csharp
        // Quick-Target Cycler (keyboard): Ctrl+Alt+ ] next, [ prev, 1-9 jump-to-slot, 0 clear.
        // Foreground-gated + debounced. Reads keys to change the overlay's active target — sends nothing
        // to the game. Cycle keys are [ ] (0xDB/0xDD), NOT arrows (Ctrl+Alt+Arrow rotates Intel displays).
        if (_settings.EnableTargetHotkeys && DateTime.UtcNow >= _nextCycleAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd
            && Down(0x11) && Down(0x12))   // Ctrl + Alt held
        {
            var fired = true;
            if (Down(0xDD)) Cycle(CycleAction.Next);          // ]
            else if (Down(0xDB)) Cycle(CycleAction.Prev);     // [
            else if (Down(0x30)) Cycle(CycleAction.Clear);    // 0
            else
            {
                fired = false;
                for (var n = 1; n <= 9; n++)
                    if (Down(0x30 + n)) { CycleToIndex(n); fired = true; break; }   // 1..9
            }
            if (fired) _nextCycleAt = DateTime.UtcNow.AddMilliseconds(250);
        }
```

- [ ] **Step 4: Add the settings round-trip**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`, find `enableGearScorer = _settings.EnableGearScorer,` (the GET projection) and add below it:

```csharp
        enableTargetHotkeys = _settings.EnableTargetHotkeys,
```

Then find `case "enableGearScorer" when TryBool(p.Value, out var b): _settings.EnableGearScorer = b; applied.Add(p.Name); break;` and add below it:

```csharp
                case "enableTargetHotkeys" when TryBool(p.Value, out var b): _settings.EnableTargetHotkeys = b; applied.Add(p.Name); break;
```

- [ ] **Step 5: Add the dashboard toggle**

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, find the `data-set="enableGearScorer"` toggle row and add this row directly after it:

```html
            <div class="row"><div class="rl">Target hotkeys<small>Ctrl+Alt+ ] next / [ prev / 1-9 slot / 0 clear &mdash; switch the active radar target</small></div>
              <label class="sw"><input type="checkbox" data-set="enableTargetHotkeys"><span class="track"></span><span class="knob"></span></label></div>
```

- [ ] **Step 6: Build + gate + test + commit**

```bash
dotnet build POE2Radar.slnx          # 0W/0E
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1   # PASS
dotnet test POE2Radar.slnx           # all pass (no test change)
git add src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(cycler): keyboard hotkeys (Ctrl+Alt+ [ ] / 1-9 / 0) + setting"
```

---

## Task 4: Controller driver (XInput) + setting

**Files:**
- Create: `src/POE2Radar.Overlay/Input/XInputNative.cs`
- Create: `src/POE2Radar.Overlay/Input/ControllerCycler.cs`
- Modify: `src/POE2Radar.Overlay/Config/RadarSettings.cs`
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (field + poll in `HandleHotkeys`)
- Modify: `src/POE2Radar.Overlay/Web/ApiServer.cs` + `Web/DashboardHtml.cs`

**Interfaces:**
- Consumes: `Cycle(CycleAction)` (Task 2).
- Produces: `ControllerCycler.Poll() → int` (-1 prev / 0 / +1 next); `RadarSettings.EnableControllerCycle`.

- [ ] **Step 1: Create `XInputNative.cs`**

Create `src/POE2Radar.Overlay/Input/XInputNative.cs`:

```csharp
using System.Runtime.InteropServices;

namespace POE2Radar.Overlay.Input;

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger, RightTrigger;
    public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

/// <summary>Read-only XInput access for controller 0. Reads the gamepad button bitmask to drive the
/// overlay's target cycler — never emits input to the game. (Win8+; a missing xinput1_4.dll is tolerated.)</summary>
internal static partial class XInputNative
{
    public const ushort GamepadLeftThumb = 0x0040;   // L3
    public const ushort GamepadRightThumb = 0x0080;  // R3
    private const uint ErrorSuccess = 0;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState(uint dwUserIndex, out XInputState pState);

    /// <summary>The button bitmask of controller <paramref name="userIndex"/>, or null when no pad is
    /// connected / the read fails / xinput is unavailable.</summary>
    public static ushort? TryGetButtons(uint userIndex = 0)
    {
        try { return XInputGetState(userIndex, out var s) == ErrorSuccess ? s.Gamepad.Buttons : null; }
        catch { return null; }
    }
}
```

- [ ] **Step 2: Create `ControllerCycler.cs`**

Create `src/POE2Radar.Overlay/Input/ControllerCycler.cs`:

```csharp
namespace POE2Radar.Overlay.Input;

/// <summary>Edge-detects L3 (prev) / R3 (next) on XInput controller 0 → a cycle direction. Read-only.</summary>
internal sealed class ControllerCycler
{
    private ushort _prev;

    /// <summary>Poll once. Returns -1 (L3 pressed = prev), +1 (R3 pressed = next), or 0. Edge-triggered:
    /// fires once per physical press. Always call it each frame so the edge state stays correct.</summary>
    public int Poll()
    {
        var read = XInputNative.TryGetButtons();
        if (read is not { } cur) { _prev = 0; return 0; }
        var pressed = (ushort)(cur & ~_prev);   // rising edges since last poll
        _prev = cur;
        if ((pressed & XInputNative.GamepadLeftThumb) != 0) return -1;
        if ((pressed & XInputNative.GamepadRightThumb) != 0) return +1;
        return 0;
    }
}
```

- [ ] **Step 3: Add the setting**

In `src/POE2Radar.Overlay/Config/RadarSettings.cs`, find `public bool EnableTargetHotkeys { get; set; } = true;` (Task 3) and add below it:

```csharp
    // Quick-Target Cycler controller support: L3 = prev target, R3 = next (both combat-dead in PoE2).
    // Read-only XInput poll. On by default; harmless when no controller is connected.
    public bool EnableControllerCycle { get; set; } = true;
```

- [ ] **Step 4: Add the field + poll**

In `src/POE2Radar.Overlay/RadarApp.cs`, find `private DateTime _nextCycleAt = DateTime.MinValue;` (Task 3) and add below it:

```csharp
    private readonly POE2Radar.Overlay.Input.ControllerCycler _controllerCycler = new();
```

Then in `HandleHotkeys`, directly after the keyboard block from Task 3, add:

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

- [ ] **Step 5: Add the settings round-trip + toggle**

In `src/POE2Radar.Overlay/Web/ApiServer.cs`, after `enableTargetHotkeys = _settings.EnableTargetHotkeys,` add:

```csharp
        enableControllerCycle = _settings.EnableControllerCycle,
```

After the `case "enableTargetHotkeys" ...` line add:

```csharp
                case "enableControllerCycle" when TryBool(p.Value, out var b): _settings.EnableControllerCycle = b; applied.Add(p.Name); break;
```

In `src/POE2Radar.Overlay/Web/DashboardHtml.cs`, after the `data-set="enableTargetHotkeys"` row add:

```html
            <div class="row"><div class="rl">Controller target cycle<small>L3 = previous target, R3 = next (combat-dead buttons in PoE2)</small></div>
              <label class="sw"><input type="checkbox" data-set="enableControllerCycle"><span class="track"></span><span class="knob"></span></label></div>
```

- [ ] **Step 6: Build + gate + test + commit**

```bash
dotnet build POE2Radar.slnx          # 0W/0E
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1   # PASS — XInputGetState is a READ, not flagged
dotnet test POE2Radar.slnx           # all pass
git add src/POE2Radar.Overlay/Input/XInputNative.cs src/POE2Radar.Overlay/Input/ControllerCycler.cs src/POE2Radar.Overlay/Config/RadarSettings.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Web/ApiServer.cs src/POE2Radar.Overlay/Web/DashboardHtml.cs
git commit -m "feat(cycler): XInput controller (L3 prev / R3 next) + setting"
```

---

## Task 5: On-screen cycle indicator (renderer)

**Files:**
- Modify: `src/POE2Radar.Overlay/Overlay/RenderContext.cs` (context field)
- Modify: `src/POE2Radar.Overlay/RadarApp.cs` (pass `_cycleIndicator` into the context)
- Modify: `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` (draw it)

**Interfaces:**
- Consumes: `CycleIndicator` (Task 2); the `RenderContext` build site + the renderer's text-draw path.

- [ ] **Step 1: Add the context field**

In `src/POE2Radar.Overlay/Overlay/RenderContext.cs`, find the `CameraMatrix` field in the `RenderContext` record (the line `float[]? CameraMatrix,`) and add directly below it:

```csharp
    CycleIndicator? CycleIndicator,
```

- [ ] **Step 2: Pass it in when building the context**

In `src/POE2Radar.Overlay/RadarApp.cs`, find where the `RenderContext` is constructed (search for `CameraMatrix:` in the `new RenderContext(` / context-build call within `Tick`/the draw path) and add the argument alongside it:

```csharp
            CycleIndicator: (_cycleIndicator is { } ci && DateTime.UtcNow < ci.Expiry) ? ci : null,
```

(If the `RenderContext` is built positionally rather than by name, insert `_cycleIndicator`-with-expiry-check in the position matching the new `CycleIndicator?` field added in Step 1.)

- [ ] **Step 3: Draw the indicator**

In `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs`, find the main draw method (the one that already calls `DrawNameplates(rt, ctx)` / the HUD draws) and add a call + method. Add the call near the other HUD draws:

```csharp
                DrawCycleIndicator(rt, ctx);
```

And add the method (model the brush/text on the existing HUD text draws in this file — reuse the existing text format/brush helper; this uses a simple top-center label):

```csharp
    /// <summary>The transient "active target" indicator drawn briefly after a Quick-Target cycle.</summary>
    private void DrawCycleIndicator(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CycleIndicator is not { } ci) return;
        var text = ci.Category.Length > 0
            ? $"▸ {ci.Pos}/{ci.Total}  {ci.Name}  ({ci.Category})"
            : $"▸ {ci.Pos}/{ci.Total}  {ci.Name}";
        var layout = new Vortice.Mathematics.Rect(0, 12, rt.Size.Width, 40);
        rt.DrawText(text, _hudTextFormat, layout, _hudBrush);
    }
```

**Note for the implementer:** reuse whatever text format + brush this renderer already uses for HUD text (e.g. the fields backing the existing FPS/HUD draw — search this file for `DrawText(`); name them as they actually are (`_hudTextFormat`/`_hudBrush` above are placeholders for the real fields). Center-align via the existing text format if one is centered, else left-aligned at x≈12 is acceptable. Do not introduce a new font/brush if a HUD one exists.

- [ ] **Step 4: Build + gate + test + commit**

```bash
dotnet build POE2Radar.slnx          # 0W/0E
powershell -ExecutionPolicy Bypass -File scripts/compliance-gate.ps1   # PASS
dotnet test POE2Radar.slnx           # all pass
git add src/POE2Radar.Overlay/Overlay/RenderContext.cs src/POE2Radar.Overlay/RadarApp.cs src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs
git commit -m "feat(cycler): on-screen active-target indicator"
```

---

## Task 6: Docs — upstream-merge + release checklist

**Files:**
- Modify: `docs/upstream-merge.md`, `docs/release-checklist.md`

- [ ] **Step 1: `docs/upstream-merge.md`** — under "What POE2GPS adds on top of Sikaka", add:

```markdown
- **Quick-Target Cycler** (`Core/Navigation/TargetCycler.cs`, `Overlay/Input/XInputNative.cs` +
  `ControllerCycler.cs`). Hooks: `_rankedTargets`/`_activeTargetId`/`_cycleIndicator`/`_nextCycleAt`/
  `_controllerCycler` fields; `BuildRankedTargets` + `Cycle`/`CycleToIndex`/`ApplyActive`/`SetActiveTarget`;
  the `_rankedTargets = …` publish after `_navTargets = BuildNavTargets`; the keyboard + controller blocks
  in `HandleHotkeys`; `RadarSettings.EnableTargetHotkeys`/`EnableControllerCycle` + the `/api/settings`
  round-trip + dashboard toggles; the `RankedTarget`/`CycleIndicator` records + the `RenderContext.CycleIndicator`
  field + `OverlayRenderer.DrawCycleIndicator`. All read-only (reads keys/XInput; never `SendInput`).
```

- [ ] **Step 2: `docs/release-checklist.md`** — under the manual live-game section, add:

```markdown
- [ ] **Quick-Target Cycler (keyboard):** in a zone, Ctrl+Alt+] / [ cycles the active radar target
      next/prev (priority then distance); Ctrl+Alt+1-9 jumps to a slot; Ctrl+Alt+0 clears. The on-screen
      "▸ N/M name" indicator shows + fades; the route follows the active target. Only fires while PoE2 is
      focused.
- [ ] **Quick-Target Cycler (controller — needs an XInput pad):** L3 = prev, R3 = next cycle the same way.
      Confirm normal gameplay is unaffected (R3 still toggles PoE2's life/mana number display — expected).
```

- [ ] **Step 3: Commit**

```bash
git add docs/upstream-merge.md docs/release-checklist.md
git commit -m "docs(cycler): upstream-merge hooks + release-checklist items"
```

---

## Self-Review

**Spec coverage** (§ = spec section):
- §3 data flow (drivers → rank → cycler → selection → indicator) → Tasks 2–5. ✓
- §4 cycle core (Next/Prev/AtIndex, id-stable) → Task 1 (tested). ✓
- §5 ranking (Rank covered + uncovered by distance) → Task 2 `BuildRankedTargets`. ✓
- §6 single-active replace → Task 2 `SetActiveTarget`. ✓
- §7 keyboard (Ctrl+Alt+ [ ] / 1-9 / 0, foreground-gated, debounced) → Task 3; controller (XInput L3/R3) → Task 4. ✓
- §8 feedback indicator → Tasks 2 (state) + 5 (render). ✓
- §9 components → Tasks 1–5. ✓
- §10 compliance (reads only, gate green) → gate run in Tasks 2–5; `XInputGetState` is a read. ✓
- §11 testing (TargetCycler pure; drivers build/gate; controller manual) → Task 1 + Task 6 checklist. ✓
- §12 out of scope (rebind UI, multi-pad, controller-slots) → not built. ✓

**Placeholder scan:** one intentional implementer note in Task 5 Step 3 (reuse the real HUD font/brush field names) — flagged explicitly because the exact field names live in unchanged renderer code; everything else is concrete.

**Type consistency:** `TargetCycler.Next/Prev/AtIndex` (Task 1) are called with `List<string>` ids in Task 2. `RankedTarget(Id,Name,Category)` + `CycleIndicator(Pos,Total,Name,Category,Expiry)` (Task 2, RenderContext) are used identically in Tasks 2/5. `CampaignObjectives.Rank` returns `RankedObjective(Id,Label,Category,Priority,DistanceSq)` — Task 2 reads `.Id/.Label/.Category`. `Cycle(CycleAction)`/`CycleToIndex(int)` (Task 2) are called in Tasks 3/4. `ControllerCycler.Poll()→int` (Task 4) matches its caller. `EnableTargetHotkeys`/`EnableControllerCycle` (Tasks 3/4) match the settings round-trip + toggles. Nav-target ids (`"t:<key>"`/`"e:<id>"`) match `Rank`'s ids (verified in code).
