# POE2GPS v0.1.9 — QoL Bundle (Menu Chord · Monolith Toggle · Gear Scorer v2)

**Date:** 2026-06-22
**Status:** Design — approved direction, pending spec review
**Targets release:** v0.1.9

## Why

Live-testing feedback on v0.1.8 surfaced three independent QoL gaps. This bundle fixes all three
without touching the read-only/compliance invariants (no input emission, no process writes, no
pricing). The in-game gear-score overlay (drawing a score on a hovered item) is **explicitly out of
scope** here — it needs a separate live RE discovery of the inventory-panel UI element and is tracked
as its own follow-up.

The three pieces are deliberately bundled into one release because each is small, they share no code,
and the user wants to test them together in one build. They are sequenced **menu chord → monolith
toggle → gear v2** (the user asked for the menu chord first).

## Global constraints (inherited by every task)

- **.NET 10, `net10.0-windows`, x64.** `TreatWarningsAsErrors=true`, `Nullable=enable` — 0 warnings, 0 errors.
- **Strictly read-only / GGG-compliant.** No `SendInput`/`PostMessage`/`keybd_event`/`mouse_event`,
  no `WriteProcessMemory`/`VirtualProtectEx`/injection, no pricing layer. `scripts/compliance-gate.ps1`
  must print PASS. All new input is **read-only** (XInput button reads, `GetAsyncKeyState`) and
  **foreground-gated** (acts only when PoE2 is the foreground window).
- **Syncability.** New logic lives in new/owned files with thin hooks into the high-churn shared files
  (`RadarApp.cs`, `RenderContext.cs`, `ApiServer.cs`, `DashboardHtml.cs`, `RadarSettings.cs`); do not
  restructure those files. See `docs/upstream-merge.md`.
- **No identifying data** in any API payload (no character name, no raw addresses).
- Tests reference `POE2Radar.Core` only; pure logic is unit-tested there.

## Branding note (folded in, no separate task)

Two user-visible strings still say "POE2Radar" and are fixed wherever a task already edits that line:
- The nav-menu chip text — `OverlayRenderer.cs:848` `const string chip = "POE2Radar";` → `"POE2GPS"`
  (Task 1 edits this method).
- The update-check console line — `RadarApp.cs` ~`Console.WriteLine($"POE2Radar v{u.Current}…")` →
  `"POE2GPS v{…}"` (Task 4, a one-line fix).

(The deeper console glow-up — ASCII art, theme, hotkey panel — remains a separate deferred item.)

---

## Part 1 — Menu toggle chord (L3+R3 / Ctrl+Alt+M / chip-click)

### What it does

Toggles the in-overlay navigation menu's **expanded** state (`RadarApp._navMenuExpanded`) — the
top-left "POE2GPS" panel whose dropdown is your GPS-able target list (`DrawNavMenu`,
`OverlayRenderer.cs:821`). Expanded = the list is open; collapsed = just the chip. Three ways to toggle:

- **Controller:** press **L3 + R3 together**. Fires once on the rising edge of "both sticks down" and
  **suppresses** the single-stick prev/next cycle for that press (so opening the menu never also flips
  your active target).
- **Keyboard:** **Ctrl+Alt+M** (M = menu) — same Ctrl+Alt namespace as the cycler keys; foreground-gated
  + debounced (~250 ms).
- **Mouse:** click the chip — the existing `"menu-toggle"` click path (`RadarApp.cs:1192`), unchanged.
  (Per the user's choice, no new global mouse-button bind.)

### Design

`ControllerCycler.Poll()` (`Input/ControllerCycler.cs`) changes return type from `int` to a small
record so one poll reports both intents:

```csharp
internal readonly record struct ControllerInput(int Cycle, bool MenuToggle);
```

- Compute `bothDown = L3 set && R3 set` from the current button mask.
- On the **rising edge** of `bothDown` (down now, not last poll) → `MenuToggle = true`, `Cycle = 0`.
- While `bothDown` (including the edge frame) → `Cycle = 0` (suppress single-stick cycling).
- Otherwise → the existing rising-edge L3=-1 / R3=+1 logic, `MenuToggle = false`.
- No pad / read fails → `default` (resets edge state), as today.

Releasing one of two held sticks creates no rising edge (`pressed = cur & ~_prev`), so no stray cycle
on release. A 1-frame skew on the way *in* (one stick lands a frame before the other) can still emit one
cycle before the toggle; acceptable for a personal QoL tool (the cycle is cheap, visible, reversible) and
noted in the code comment. No new timers.

`RadarApp.HandleHotkeys()` consumes the new shape:

- Controller block (gated by `EnableControllerCycle`): read `var input = _controllerCycler.Poll();`
  Foreground-gated, if `input.MenuToggle` → `_navMenuExpanded = !_navMenuExpanded;` else if
  `input.Cycle != 0` → `Cycle(...)` (unchanged behavior).
- Keyboard block (gated by `EnableTargetHotkeys`): when `Ctrl(0x11)+Alt(0x12)+M(0x4D)` are down,
  foreground-gated + debounced → `_navMenuExpanded = !_navMenuExpanded;`. Reuse a debounce instant
  (the existing `_nextCycleAt` style; a dedicated `_nextMenuAt` keeps it independent of the cycle
  debounce).

### Compliance

No new Win32 surface: XInput button reads and `GetAsyncKeyState` are already in use by the shipped
cycler. Gate stays green.

### Tests (Core-only constraint)

`ControllerCycler` lives in `Overlay`, not `Core`, so it has no existing unit tests. To keep the
chord logic testable, **extract the pure edge-detection into a testable seam**: a pure static method
(or a tiny `Core`-side helper) `ControllerChord.Resolve(ushort prev, ushort cur) -> ControllerInput`
that `ControllerCycler` calls with the live mask. Unit-test the seam in `tests/POE2Radar.Tests`:

- both-down rising edge → `MenuToggle=true, Cycle=0`
- both-down held (prev already both) → `MenuToggle=false, Cycle=0`
- L3-only rising → `Cycle=-1`; R3-only rising → `Cycle=+1`
- release one of two held → `Cycle=0, MenuToggle=false` (no stray)
- no-change frame → `default`

If `ControllerInput`/`ControllerChord` must live in `Core` for the test project to see them (it
references Core only), place the pure record + resolver in `POE2Radar.Core` (`Core/Input/` or
`Core/Navigation/`) and have the Overlay `ControllerCycler` consume it. The XInput P/Invoke stays in
Overlay.

---

## Part 2 — Monolith reward panel: default off + Settings toggle

### What it does

The in-overlay "nearby monolith rewards" panel drawn over the minimap is `ShowMonolithPanel`
(`RenderContext`) ← `MonolithSettings.ShowPanel` (`RadarSettings.cs`). It currently defaults **on** and
is not surfaced in the dashboard, so the user can't turn it off. This:

- Flips `MonolithSettings.ShowPanel` default → **false**.
- Surfaces a **"Show monolith reward panel"** toggle in the dashboard Settings tab.
- Round-trips it through `/api/settings` (flat GET projection + `ApplySettings` case), matching the
  established nested-settings pattern (the panel toggle reads/writes `_settings.Monoliths.ShowPanel`).

Leaves `MonolithSettings.Enabled` and `ShowMapLabel` alone (map labels still work; only the panel is
off by default). `HighlightMinEx`/`MinRewardEx`/`HideCollected`/`PanelMaxDistance` are untouched.

### Immediate relief (documented to the user, no build needed)

Edit the unzipped `config/radar_settings.json` → `"monoliths": { "showPanel": false }` → relaunch.

### Design

- `RadarSettings.cs`: `public bool ShowPanel { get; set; } = false;` (was `true`).
- `/api/settings` flat GET: add `monolithShowPanel = _settings.Monoliths.ShowPanel,`.
- `ApplySettings`: `case "monolithShowPanel" when TryBool(v, out var b): _settings.Monoliths.ShowPanel = b; break;`
- Dashboard Settings tab: a toggle row `data-set="monolithShowPanel"` wired by the generic binding.
- The render hook already exists (`ShowMonolithPanel: _settings.Monoliths.ShowPanel` in the
  `RenderContext` build, or equivalent) — verify it reads the flag, don't add new render code.

### Tests

Settings round-trip is config plumbing (no pure-logic unit). Covered by the manual live check
(toggle off → panel disappears; toggle on → reappears).

---

## Part 3 — Gear Scorer v2 (un-break + UI love + grid heatmap)

### Problem (confirmed live)

The read port works — 48 inventory items read with correct names/rarity/affixes — but **every item
scores 0** because `GearWeightStore` ships with no weights, and the dashboard can't help the user build
weights because `/api/gear` **omits the stat ids** the scorer matches on. The Weights panel even tells
the user to "copy a stat id from an affix above," but no stat id is shown.

### Scope (per the user's choice)

Dashboard un-break + UI polish **and** an inventory **grid heatmap**. The in-game tooltip/overlay is a
separate live-discovery track (not in this release).

### 3a. Expose stat ids in `/api/gear`

`AffixContribution` (the scored-affix record) does **not** currently carry stat ids — `Affix` does, but
the scorer's output (`AffixContribution(Line, Value, Weight, Points)`) drops them. Add `StatIds` to the
contribution so the dashboard can render + one-click them:

- `Core/Gear/GearScorer.cs`: `AffixContribution(string Line, IReadOnlyList<string> StatIds, double Value, double Weight, double Points)`; populate `StatIds = a.StatIds` in `Score`.
- `GearJson` (`RadarApp.cs:555`): add `statIds = a.StatIds` to the affix projection.

This is the keystone: it makes the starter weight set verifiable against the user's real items (the ids
shown are exactly the ids the scorer keys on).

### 3b. Starter weight set (shipped, editable)

`GearWeightStore` seeds a **starter weight set** when no `config/stat_weights.json` exists yet (first
run / fresh install), instead of loading empty. The starter set weights common build-defining stats:
maximum life, the four elemental + chaos resistances, movement speed, %physical/elemental damage,
attack speed, cast speed, critical chance/multiplier, and flat added damage — each at a sensible
relative weight (e.g. life/res high, speed medium).

- The exact stat-id strings are **pinned in the implementation plan** by extracting them from the
  embedded `src/POE2Radar.Core/Game/poe2_mod_stats.json` (the modId→statId map the scorer uses) and
  **validated against the live 48-item read** once 3a exposes the ids — so the starter weights provably
  hit real affixes rather than guessed strings.
- Seeding only happens when the config file is **absent**; an existing user file is never overwritten.
- A dashboard **"Load starter weights"** button (POST `/api/gear-weights {reset:"starter"}`) lets a
  user who already has an (empty) file pull in the starter set on demand. The store exposes
  `LoadStarter()` (replaces current weights with the embedded defaults, saves).

### 3c. One-click weighting

Each affix row in the items list renders its stat id(s) as clickable chips. Clicking a chip adds that
stat id at a default weight (e.g. 1) via the existing `POST /api/gear-weights {setWeight:{statId,weight}}`
— no copy-paste. The manual "stat id + weight" inputs stay for fine-tuning. (The scorer takes the MAX
weight over an affix's ids, so weighting any one id of a multi-id affix is sufficient.)

### 3d. UI love — rarity colors + roomier list

- Color each item's name by **rarity** (Normal/Magic/Rare/Unique → the game's white/blue/yellow/orange),
  reusing the dashboard's existing rarity color treatment if present, else a small local map.
- A roomier, less cramped item list (the current single scroll strip is the complaint): give the list
  real vertical room and per-item cards with the score badge, god-roll star, rarity-colored name, and
  the affix chips.

### 3e. Inventory grid heatmap

A grid view (toggle/segment alongside the list) that lays inventory items out and colors each cell on a
**green→red** ramp by its 0–100 score (green = high). Grouped by inventory.

- **Positional vs. flat:** a faithful grid needs each item's **slot index** + the inventory's **box
  dims** (X×Y). `Poe2Live.ReadInventory` already reads `Slot` per item and `TotalBoxes` per inventory
  (validated offsets — see CLAUDE.md inventory notes). The plan **verifies** whether the current
  `ReadInventory` return type surfaces slot + box dims; if not, it adds them (read-only Core addition)
  and threads `slot` + `boxW`/`boxH` into `ScoredItem`/`GearJson`. If exposing positions proves
  out of scope, the grid falls back to a **flat wrapped grid** of score-colored cells (still grouped by
  inventory) — the heatmap reads the same either way. The plan picks positional if the read is already
  there, flat otherwise, and says which.
- Cell shows the score; hover/title shows the item name + affixes. Clicking a cell is not required.

### Compliance

All additions are read-only (inventory reads already exist and are gated by `EnableGearScorer`, default
off). No input/write/pricing APIs. Gate stays green.

### Tests

- `GearScorer` already has unit tests; extend for the `AffixContribution.StatIds` field and a
  weighted-scoring case proving a starter-weighted affix yields nonzero points.
- `GearWeightStore`: a test that a fresh store (no file) loads the **starter** weights (nonempty), and
  that an existing file is **not** overwritten by seeding. (`GearWeightStore` is in Overlay; if the test
  project can't see it, test the pure starter-defaults provider — a `Core`-side `StarterWeights.Default`
  dictionary — and have the store consume it.)

---

## File map (new vs. hook)

**New / owned files:**
- `Core/Input/ControllerChord.cs` (or `Core/Navigation/`) — pure `ControllerInput` record +
  `Resolve(prev,cur)` seam (Part 1).
- `Core/Gear/StarterWeights.cs` — the embedded starter weight dictionary (Part 3b), Core-side so tests
  see it.
- Tests in `tests/POE2Radar.Tests` for the chord seam, starter weights, and gear scoring.

**Thin hooks into shared files (do not restructure):**
- `Input/ControllerCycler.cs` — call the seam; return `ControllerInput`.
- `RadarApp.cs` — `HandleHotkeys` controller + keyboard blocks; `_nextMenuAt`; `GearJson` statIds +
  grid fields; the console branding one-liner.
- `OverlayRenderer.cs:848` — chip text → "POE2GPS".
- `RadarSettings.cs` — `ShowPanel` default false.
- `Web/ApiServer.cs` — `/api/settings` projection + `ApplySettings` case for `monolithShowPanel`;
  `/api/gear-weights` `{reset:"starter"}` action.
- `Web/GearWeightStore.cs` — seed starter on absent file; `LoadStarter()`.
- `Web/DashboardHtml.cs` — Settings monolith toggle row; Gear tab: rarity colors, roomier cards, affix
  stat-id chips (one-click), "Load starter weights" button, grid-heatmap view.

## Out of scope (separate tracks)

- **In-game gear-score overlay** (score on a hovered item / over the real inventory panel) — needs live
  RE discovery of the inventory-panel UI element. Its own spec after discovery.
- **Console glow-up** beyond the branding fix (ASCII art, theme, hotkey panel).
- **Richer Director/Entity-Atlas cataloging** (checkpoints/NPCs/flags/league events) + the
  backward-door classification. Tracked in the director roadmap.

## Success criteria

1. L3+R3 toggles the top-left nav list open/closed without flipping the active target; Ctrl+Alt+M does
   the same; chip-click still works; chip reads "POE2GPS".
2. The monolith reward panel is gone by default; the Settings toggle turns it on/off live.
3. With the scorer on, items show **nonzero** scores out of the box (starter weights); each affix shows
   its stat id(s); clicking a chip adds a weight; the list shows rarity colors and a grid heatmap.
4. Build 0W/0E, all tests pass, compliance gate PASS, `/api/gear` carries no identifying data.
