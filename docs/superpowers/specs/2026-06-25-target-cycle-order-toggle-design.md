# Target Cycle Order Toggle — Design

**Date:** 2026-06-25
**Status:** Design approved. Branch `feat/cycle-order-toggle` off `main`.

## Summary

The Quick-Target Cycler (controller L3/R3 + keyboard Ctrl+Alt+`[`/`]`) currently steps through a
priority-then-distance ("intelligent") ranking that does **not** match the order shown in the top-left
nav-menu dropdown — so cycling feels like it jumps around. This feature:

1. Makes the **default** cycle follow the **radar-menu order** (the dropdown's order: landmarks/tiles,
   then nearest entities). R3 / `]` = next *down the list*, L3 / `[` = up, wrapping.
2. Demotes the priority/distance ranking to an opt-in **"Intelligent target cycling"** toggle at the
   top of Settings, **off by default**.
3. Adds **hold-to-fast-cycle** (tap = one step; hold past ~400 ms → auto-repeat ~150 ms) on **both**
   the controller and the keyboard.

Read-only throughout. Nothing about *what* is cycled or how the active target is applied changes — only
the **order** and the **hold** behavior.

## Background (current behavior, verified)

- `RadarApp.BuildNavTargets(player)` → `_navTargets`: landmarks/tiles (in landmark order), then entity
  POIs ordered by distance. This is exactly what the nav-menu dropdown shows (`DrawNavMenu` iterates
  `ctx.Legend`, which `BuildLegend` builds one-per-`_navTargets`). **This is the "radar-menu order."**
- `RadarApp.BuildRankedTargets(player)` → `_rankedTargets`: Director catalog priority (desc) then
  distance — the "intelligent" order. Built only when a cycler input is enabled.
- `TargetCycler.Next/Prev/AtIndex` (pure, `Core/Navigation`): cycle over an ordered id list; today fed
  `_rankedTargets` ids. **Unchanged by this feature.**
- Controller: `ControllerCycler.Poll()` → `ControllerChord.Resolve(prev,cur)` → `ControllerInput(Cycle,
  MenuToggle)`: L3 rising = -1, R3 rising = +1, L3+R3 = menu toggle (suppresses cycle while both held).
- Keyboard: Ctrl+Alt+`[` (VK 0xDB) / `]` (0xDD) = prev/next, debounced via `_nextCycleAt`; Ctrl+Alt+M =
  menu toggle.
- Cycling is **on by default**: `RadarSettings.EnableTargetHotkeys` + `EnableControllerCycle` default true.

## Design

### 1. Cycle source = menu order by default

- New `RadarSettings.IntelligentTargetCycling` (bool, **default `false`**).
- A private RadarApp helper returns the active cycle id-list:
  - `false` (default): `_navTargets` ids in order → **menu order**.
  - `true`: `_rankedTargets` ids in order → priority/distance.
- `Cycle(direction)` and the slot path (`CycleToSlot`) consume this helper instead of `_rankedTargets`
  directly. `TargetCycler` is untouched — it just receives a different ordered list. The "▸ N/M name"
  cycle indicator reflects whichever list is active.
- `_rankedTargets` is built only when `(EnableTargetHotkeys || EnableControllerCycle) && IntelligentTargetCycling`
  — skip the work when the toggle is off.
- The nav-menu **display is unchanged**; the cycle now simply follows it.

### 2. Hold-to-fast-cycle — `Core/Input/HoldRepeat.cs` (new, pure)

A small stateful helper with an injected clock so it is fully unit-testable:

```
int Update(int heldDir, DateTime now)   // returns steps to fire this poll (0 or 1)
```
- `heldDir`: 0 = nothing held; -1 = prev held; +1 = next held.
- On a direction change (including 0→X): fire **1** step immediately (the tap) and reset the hold clock.
- While the same direction stays held: fire 0 until `now - holdStart >= InitialDelay`, then fire 1 each
  time `now - lastFire >= Interval`.
- On release (heldDir 0): reset; fire 0.

Tunables on `RadarSettings`: `CycleHoldDelayMs` (default 400), `CycleHoldIntervalMs` (default 150).

**Wiring (RadarApp, two `HoldRepeat` instances — one per input):**
- **Controller:** `ControllerCycler.Poll()` additionally reports the currently-**held** cycle direction
  (L3 down = -1, R3 down = +1, both down = 0 (menu mode, suppressed), none = 0). RadarApp feeds that to
  a controller `HoldRepeat`; for each step it returns, calls `Cycle`. The L3+R3 `MenuToggle` edge still
  toggles the nav-menu. The old rising-edge `Cycle` is replaced by `HoldRepeat`'s first-step-on-press
  (no double-step).
- **Keyboard:** RadarApp computes held dir = `Ctrl&&Alt && (Down('[') ? -1 : Down(']') ? +1 : 0)`,
  feeds a keyboard `HoldRepeat`; replaces the `_nextCycleAt` debounce. Foreground-gated as today.

Both stay read-only — they read input STATE and never emit input.

### 3. Settings UI

- `/api/settings` round-trips `intelligentTargetCycling` (the hold tunables stay config-file-only;
  not surfaced in the dashboard to avoid clutter).
- Dashboard Settings tab: a new toggle row at the **very top** — **"Intelligent target cycling"** with
  the hint *"On = smart priority/distance order · Off = follow the radar menu."* Default off, like the
  other toggles (`renderSettings`/save pattern).

## Out of scope

- No change to `EnableTargetHotkeys` / `EnableControllerCycle` — cycling stays on by default.
- No change to the nav-menu display, the active-target application, routes, or the slot hotkeys'
  meaning (Ctrl+Alt+1–9 still pick the Nth of the *active* list).
- No overlay Console-settings change (the dashboard is the settings surface).

## Compliance

Read-only end to end: reads keyboard / XInput state, never `SendInput`; no process writes; no pricing.
`scripts/compliance-gate.ps1` + `scripts/scrub-strings.ps1 -SelfTest` must PASS; build 0 warnings / 0 errors.

## Testing

- `HoldRepeat` unit tests (injected clock): tap fires 1; hold < delay fires 0; after delay repeats at
  interval; direction flip re-taps immediately; release resets.
- `TargetCycler` already covered.
- Build + gate; manual in-game smoke: menu-order cycle by default, toggle flips to priority/distance,
  hold-to-fast on both controller and keyboard, L3+R3 still opens the menu.
