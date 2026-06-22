# Quick-Target Cycler — Design Spec

- **Date:** 2026-06-22
- **Status:** Approved 2026-06-22 (Ryan). Keyboard + controller, single-active, priority-then-distance.
- **Topic:** Hotkeys + controller stick-clicks to quickly switch the radar's active nav target up/down a
  ranked list — manual target control as a bridge until the Objective Director is fully tuned.

---

## 1. Summary & the user need

The Director auto-routes, but it's still maturing. In the meantime the user wants to **steer manually,
fast** — tap a key (or a controller stick) to switch which objective the radar is routing to, without
opening the dashboard or hunting with the mouse. This adds a **single "active target" you can cycle**
(next/prev) or jump to by number, ordered the same way the Director ranks things, driven from both
**keyboard** and **XInput controller**.

It is **100% read-only**: it *reads* key/controller state to change the overlay's own selection — it never
*sends* input to the game. (`GetAsyncKeyState` is already used for F6/F7/etc.; `XInputGetState` is a new
**read** P/Invoke. No `SendInput`. The compliance gate stays green.)

## 2. Invariants

- **I1 — Read-only.** Reads keyboard + controller state to drive the overlay's selection. No input
  emission, no process writes. Gate stays green.
- **I2 — Foreground-gated.** Hotkeys/controller only act while PoE2 is the focused window, so cycling
  never fires while you're in another app (consistent with the overlay's existing focus behavior).
- **I3 — Reuse the nav pipeline.** A cycle/select sets `_selectedIds` (the existing selection) and the
  existing selection→route machinery does the rest. No parallel routing system.
- **I4 — Reuse the Director's ranking.** The list order is the Director's `Rank` (priority desc, distance
  asc); no new ranking logic, just applied to the full nav-target list.
- **I5 — Pure, testable core.** The cycle/select index logic is a pure unit (`TargetCycler`) with no
  game/UI dependency.

## 3. Architecture & data flow

```
render tick (HandleHotkeys, foreground-gated)
   ├─ keyboard driver: edge-detect Ctrl+Alt+{ [ ] 1..9 0 }
   └─ controller driver: XInputGetState(0) → edge-detect L3 / R3
            │  (both produce one logical action: Next / Prev / AtIndex(n) / Clear)
            ▼
   rank the current _navTargets  →  ordered List<string> ids   (catalog priority desc, then distance asc)
            ▼
   TargetCycler.Apply(action, rankedIds, currentActiveId)  (PURE)  →  new active id (or null)
            ▼
   set selection: _selectedIds := [ activeId ]   (under _navLock; reuses the route pipeline)
            ▼
   feedback: flash an on-screen "▸ <pos>/<total>  <label> (<category>)" indicator (~2 s); dashboard
             shows the ranked list with the active marker.
```

## 4. The cycle core (`Core/Navigation/TargetCycler.cs`, pure + unit-tested)

`TargetCycler` is **stateless pure logic** over an already-ranked `IReadOnlyList<string>` of target ids
plus the current active id:

- `string? Next(rankedIds, current)` — the id after `current` in the ranked list; wraps to the first;
  if `current` is null/missing, returns the first.
- `string? Prev(rankedIds, current)` — symmetric; wraps to the last.
- `string? AtIndex(rankedIds, n)` — the id at 1-based slot `n` (Ctrl+Alt+1 = first); null if out of range.
- `Clear()` is just "active id := null" (handled by the caller).

**Key property — id-stable, not index-stable.** Targets despawn and the list rebuilds every tick, so the
cycler always re-finds `current`'s position in the *fresh* ranked list before moving ±1. If `current`
vanished, Next/Prev fall back to the first. This is the whole reason it's a pure function over (list,
current) rather than a stored index — fully unit-testable: empty list, single item, wrap-around at both
ends, current-missing, duplicate guard.

## 5. Ranking (priority, then distance)

Order the full `_navTargets` list (tiles + entity POIs) by **catalog priority (desc), then distance from
the player (asc)** — the exact order the Director's `CampaignObjectives.Rank` already produces, extended
to *all* targets: a target covered by an enabled catalog objective takes that objective's priority;
uncatalogued targets take priority 0 and sort by distance after the prioritized ones. So slot #1 is the
highest-priority nearest objective, and cycling walks down the Director's pick order. Recomputed on each
keypress (off the hot loop; the list is at most a few dozen entries).

## 6. Selection model — single active (replace)

A cycle/number/select action replaces `_selectedIds` with **just the one active target** and routes to it.
F6 "add nearest" multi-select still works; a cycle press simply collapses the selection to the single
target you're steering toward. Clear drops it (no active route). This matches "change *your* radar target"
(singular). (Considered + rejected: layering onto multi-select — more complex, and not what's wanted.)

## 7. Input drivers

Both run inside `HandleHotkeys` (render thread, foreground-gated), both **edge-detected** (one action per
press, not per frame), both call the same core + selection setter.

- **Keyboard** (extends the existing `GetAsyncKeyState` hotkey block):
  - `Ctrl+Alt+]` = Next · `Ctrl+Alt+[` = Prev · `Ctrl+Alt+1..9` = jump to slot N · `Ctrl+Alt+0` = Clear.
  - Chord = `Down(VK_CONTROL) && Down(VK_MENU) && Down(<key>)`, with per-key rising-edge tracking.
  - **Cycle keys are `[`/`]` (VK_OEM_4/VK_OEM_6), NOT the arrow keys** — `Ctrl+Alt+Arrow` is the
    Intel-graphics screen-rotation shortcut on many machines and would flip the user's display.
- **Controller (XInput)** — NEW, read-only:
  - `XInputGetState(0)` from `xinput1_4.dll`; read `Gamepad.wButtons`; bits `XINPUT_GAMEPAD_LEFT_THUMB`
    (0x0040 = L3) and `XINPUT_GAMEPAD_RIGHT_THUMB` (0x0080 = R3).
  - **L3 = Prev · R3 = Next** (bare clicks — both are combat-dead in PoE2: L3 is menu-nav only, R3 only
    toggles the life/mana number display). Rising-edge on each bit. Honest note: R3 also flickers PoE2's
    life/mana display each press — cosmetic, accepted.
  - Reading the pad does not interfere with the game reading it (reads don't consume input).

## 8. Feedback

So you can drive it without the dashboard: on each cycle, flash a small on-screen indicator near the
player blip — `▸ 2/7  Mausoleum  (Side Boss)` — fading after ~2 s (config-driven style, reusing the
renderer's text path). The dashboard's existing nav/legend view also shows the ranked list with the
active target marked.

## 9. Components / files

**New:**
- `Core/Navigation/TargetCycler.cs` — the pure cycle core (Next/Prev/AtIndex). **Unit-tested.**
- `Overlay/Input/XInputNative.cs` — the read-only `XInputGetState` P/Invoke + the gamepad structs +
  button constants. (Mirrors the project's `LibraryImport` native style.)
- `Overlay/Input/ControllerCycler.cs` — polls XInput, edge-detects L3/R3, emits Next/Prev. (Thin.)

**Extended:**
- `Overlay/RadarApp.cs` — in `HandleHotkeys`: the keyboard chord block + the controller poll; a
  `SetActiveTarget(string? id)` helper that ranks `_navTargets`, applies the cycler action, and sets
  `_selectedIds` under `_navLock`; track `_activeTargetId`; the cycle-indicator state + render hook.
- `Overlay/Overlay/OverlayRenderer.cs` (+ `RenderContext`) — draw the brief active-target indicator.
- `Config/RadarSettings.cs` — `EnableTargetHotkeys` (default **on**), `EnableControllerCycle`
  (default **on when a pad is present**), the modifier/keybind config (rebindable later), + the
  `/api/settings` round-trip + dashboard toggles.

## 10. Compliance & safety

All reads. `GetAsyncKeyState` (existing) + `XInputGetState` (new) read input devices; neither is a
forbidden input-emission/process-write symbol, so `scripts/compliance-gate.ps1` stays green (verify after
adding `XInputNative`). Nothing is ever sent to the game. Foreground-gated (I2).

## 11. Testing

- `TargetCycler` — pure xUnit: Next/Prev wrap at both ends; AtIndex bounds; current-missing → first;
  empty list → null; single item; no-op when list unchanged.
- Ranking helper (priority-then-distance, uncatalogued → 0) — pure xUnit if extracted cleanly.
- Keyboard driver + the `SetActiveTarget`/indicator wiring — build- + gate-verified; manual in-game check.
- **Controller driver + the live feel — needs a connected XInput pad** → release-checklist manual item
  (the one part not verifiable without hardware).

## 12. Out of scope (v1)

- **Full key/button rebinding UI** — v1 ships sensible defaults (Ctrl+Alt+… , L3/R3) + a settings
  on/off; a rebind editor is a later iteration.
- **Multiple controllers / non-XInput pads** (DirectInput, PS-native) — v1 reads XInput user 0.
- **Controller number-slots** — sticks do Next/Prev only; numbered slots are keyboard-only (no number
  buttons on a pad).

## 13. Risks

- **Controller binding is unverifiable without a pad** — mitigated by `EnableControllerCycle` (off when no
  pad) + a clear release-checklist smoke-test. L3/R3 choice is research-backed (combat-dead in PoE2).
- **R3 cosmetic side effect** — each "next" toggles PoE2's life/mana number display; accepted, documented.
- **Edge-detection correctness** — both drivers must fire once per press; the pure core is unit-tested,
  the edge tracking is simple per-input rising-edge state (same idiom as the existing F10/F12 debounce).
- **Ranking cost** — recomputed per keypress over ≤ a few dozen targets; negligible, and only on a press
  (not per frame).
