# Patch-Resilience & Health/Status — Design

**Date:** 2026-06-26
**Branch:** `feat/patch-resilience`
**Status:** Approved design — ready for implementation plan

## Problem

When a PoE2 patch shifts memory offsets, POE2GPS today fails in a way the user can't diagnose:

- **Dominant symptom — startup death.** `Bootstrap.ResolveGameStateSlot` only accepts a GameState slot if the *full*
  chain resolves to a valid `LocalPlayer` (metadata `StartsWith("Metadata/")`). On a patch that shifts those offsets
  (exactly what 0.5.4 did: `LocalPlayer` 0x5A0→0x5B8), the `"Game States"` AOB still matches but no slot validates →
  `Bootstrap` returns 0 → `Program` returns 2 → the console prints `"…make sure you're loaded into a zone"` and the
  window closes. The overlay never starts, so an in-overlay banner can never draw. The *same* one-shot exit fires if the
  user launches POE2GPS at login/character-select before zoning in (a pre-existing papercut).
- **Secondary symptom — silent runtime failure.** If a *deeper* offset drifts (entity components, vitals, terrain) while
  the top-level chain still resolves, the overlay starts but draws nothing/garbage, with no signal to the user.

There is no notion in the codebase of "have we ever resolved," no graduated chain diagnostic, and no health surface. The
user can't tell "the tool is broken (needs an update)" from "I'm at a menu / still loading."

## Goal

Self-detect when POE2GPS can't read the game, distinguish "out of date with the patch" from "you're just not in a zone,"
and surface that clearly — an **overlay banner**, a **dashboard Status panel**, and a **non-fatal, self-connecting
startup** — instead of dying silently. Strictly read-only; compliance gate stays green.

## Approved approach — "always-up UI" (lazy resolve)

Attach → start `RadarApp` *immediately* (overlay window + HTTP API + dashboard all live) → resolve the GameState slot
**lazily on a background cadence**, retrying until it works, instead of blocking/exiting in `Bootstrap`. Health becomes a
first-class thing `RadarApp` owns and publishes to every surface. This is the only structure where the Status panel and
banner are available *during a hard patch-break* (the exact moment they're for), and it fixes "launched at login → instant
exit" for free. It also enables **re-attach on game restart** (in scope: after a patch you restart PoE2 and the overlay
reconnects on its own).

Rejected: (2) non-fatal Bootstrap but UI only after first resolve — a total break gives the user only a console message,
no dashboard, in the very scenario the panel is for. (3) a throwaway diagnostic HTTP server during retry — Approach 1's
benefit done the hard way.

## Detection model

The world loop runs a **graduated probe** each tick (replacing the all-or-nothing `TryResolve` for the world thread
only; the render + API threads keep using `TryResolve`). Five stages:

| Stage | Reads | Meaning |
|-------|-------|---------|
| S1 | GameState slot derefs non-zero | game memory reachable |
| S2 | an InGameState candidate found (`GameState+0x08` / `+0x48` walk) | — |
| S3 | `AreaInstance` ptr (`InGameState+0x290`) non-null | — |
| **S4** | **`AreaHash@+0x11C` ≠ 0 AND `AreaLevel@+0xC4` ∈ [0..100]** | **provably in a zone** |
| S5 | `LocalPlayer@+0x5B8` ≠ 0 AND metadata `StartsWith("Metadata/")` | full success (== today's `TryResolve`) |

**S4 is the anchor and the key insight.** `AreaHash` is a direct scalar read (no sub-pointer), sits *below* the 0.5.4
drift boundary (0x580), and is written early in area init — so it stays valid across deep-field shifts and during the
load window. Hard rules from the adversarial stress-test:

- **`AreaCode` is advisory only — never part of the S4 gate.** It's a two-hop read (`AreaInstance+0xA0`→`AreaInfo*`→
  string) that fails earlier in zone init than the scalar reads and silently returns `""` if `AreaInfoPtr` drifts. Use it
  for display/logging only.
- **`AreaLevel` range is [0..100] inclusive.** Hideouts legitimately read `AreaLevel == 0`; excluding 0 would false-alarm
  every hideout session.

### States (world loop owns the machine — single source of truth)

A single Core enum `HealthState { Waiting, Searching, Ok, Loading, NotInGame, Broken }` is the one source of truth — used
by the monitor's verdict, the `RadarState` record, the `RenderContext`, and the `/state` JSON (serialized lowercase).
There is **no** separate overlay-side enum.

- `Waiting` — not attached / PoE2 process not running.
- `Searching` — attached, slot not resolved yet (AOB scan in progress).
- `Ok` — S5 passes. **Latches `everResolved = true`.**
- `Loading` — S3 + S4 pass, S5 fails. **Benign** ("Loading area…"); *no alarm*.
- `NotInGame` — was `Ok` earlier this session, now not resolving (menu/login after play). Benign.
- `Broken` — the alarm state (update-aware message).

### Anti-false-alarm mechanisms (each from a stress-test finding)

1. **Hold-off timer.** The #1 false positive: *every* zone load briefly reads S4-true/S5-false (area written before the
   player is placed). `Loading` escalates to `Broken` only after **HOLD_OFF (25 s)** of *continuous* S4-proven-but-no-S5;
   the timer **resets whenever S4 transitions false→true** (each new zone load starts fresh). Normal loads finish well
   under 25 s; a genuine offset break never resolves S5, so it escalates correctly after the wait.
2. **S4 stability ≥ 3 consecutive ticks** before "provably in a zone" is trusted — filters ghost AOB hits where garbage
   memory coincidentally reads a plausible hash/level for a single tick.
3. **AOB-zero vs chain-drift split.** Track, per scan, *raw candidate count* vs *validated*. **Zero raw hits** = the byte
   pattern itself broke → escalate sooner with stronger wording. Hits-but-none-validate = offset drift → graduated path.

### Soft escapes (false-negative gaps — preserve the latch, never hard-claim)

4. **Post-`Ok` long-offline.** After **> 5 min** of continuous `NotInGame` following a real `Ok` (still attached +
   AOB-matched), surface a *soft* message ("radar's been offline a while — if you're in a zone, a patch may have shifted
   offsets; check for a new release"). Not a hard `Broken` claim — preserves the latch rule's intent while covering a
   mid-session break that the latch would otherwise silence forever.
5. **Radar-empty-while-`Ok`.** A deep-only drift (entities `+0x6D8` / terrain `+0x8B8`) leaves `TryResolve` passing but
   the map blank. If `Ok` but **terrain grid absent for ≥ 10 world ticks (~330 ms)**, surface a soft message ("reading
   your character but no map data — deep reads may be stale after a patch"). **Terrain-present is the anchor** (a real
   zone always has terrain; entity count can legitimately be sparse, so it is *not* used as the trigger).

### Process-death / re-attach

- After **~5 s** of S1 failure (`gameState` deref returns 0) following a prior `Ok`, query whether the PoE2 process is
  still alive. If dead → `Waiting` ("Path of Exile 2 is no longer running") and the resolver re-attaches and re-resolves
  when a new client appears — overlay survives a game restart without its own restart.
- Re-attach requires `ProcessHandle` to refresh its underlying OS handle to a new PID in place (a new
  `ProcessHandle.TryReattach()`), so the existing `MemoryReader`s keep their reader reference; the resolver then re-runs
  the AOB scan and `Rebind`s the slot. This is the **highest-risk** piece and is sequenced as the **final,
  independently-reviewable task** — cuttable without losing the core feature (non-fatal startup + lazy resolve + banner +
  Status panel all stand on their own; without it, a game restart still requires an overlay restart, as today).

## Message / wording policy

Decided: **soft + update-aware**, strictly sanitized. Messages come from a **fixed enum→string mapping** (never raw
memory addresses or exception text — they ride the unauthenticated `localhost:7777 /state`). Update-awareness is read
from the existing `UpdateChecker.Result { Current, Latest, UpdateAvailable, Url }`:

- `UpdateAvailable == true` → **"Update available — download: {Url}"**
- `UpdateAvailable == false` and `Latest != null` (we're current) → **"POE2GPS is up to date but can't read the game — try restarting in a loaded zone."**
- `Latest == null` (check not done / network failed) → **"POE2GPS can't read Path of Exile 2 — it likely just updated; a fix is coming."**

All banner/message strings are plain English and contain none of the 19 write-API / 9 input-API symbol names the
compliance gate forbids (verified safe); no helper/variable/comment may be named with a forbidden symbol.

## Architecture & components

- **Core — `OffsetHealthMonitor` (pure, clock-injected, heavily TDD'd).** The entire state machine, same idiom as
  `HoldRepeat` / `ObjectiveClassifier`. Input: a per-tick `ChainProbe` observation record + `DateTime now`; output:
  `HealthVerdict(HealthState State, string? Message)`. Every latch, timer, threshold, and the update-aware message
  selection live here, so every stress-test scenario becomes a unit test with a fake clock.
- **Core — `Poe2Live.Probe()`.** A superset of `TryResolve`: returns how far the chain got (`ResolveStage`), the S4 low
  fields (`AreaHash`, `AreaLevel`), `terrainPresent`, and the resolved handles when S5 passes (so the existing heavy walk
  proceeds unchanged). Plus a `volatile nint` slot field + `Rebind(nint slot)` that clears per-entity caches, enabling
  lazy/late binding and re-attach. `TryResolve` is reimplemented in terms of `Probe()` (S5 reached) to avoid divergence.
- **Overlay — `SlotResolver` (background thread).** Owns attach management (re-attach on process death) and the AOB scan
  on a **1.5 s** cadence until a candidate validates; on success sets the volatile slot on all three readers
  (`_live`/`_liveRender`/`_liveApi`). Publishes, as volatile fields the world loop reads into each `ChainProbe`, the
  current **attach state** and **last-scan candidate count** (the latter drives the AOB-zero signal); logs progress via
  `ConsoleTheme` (`"Waiting for in-game state (load into a zone)…"`, `"GameState slot resolved: 0x…"`, and the
  update-aware hint after sustained failure). The AOB scan is stateless/re-runnable and ~0.1–1 s; 1.5 s cadence keeps CPU
  negligible.
- **Overlay — `RadarApp` wiring.** Holds the `OffsetHealthMonitor`; in `WorldTick`, calls `Probe()`, builds the
  `ChainProbe`, calls `Evaluate`, and publishes `volatile _healthState` + `_healthMessage`. Builds the three `Poe2Live`
  readers with `slot = 0` in the ctor (TryResolve/Probe already no-op on a 0 slot); the resolver fills the slot. The
  `_live.CustomLandmarkMatch` / `CuratedLookup` / `LandmarkClusterGap` assignments stay in the ctor (slot-independent).
- **Overlay — `Program.cs`.** Drops the `Bootstrap`-blocks-and-`return 2` flow: attach → `new RadarApp(process, reader)`
  (no pre-resolved slot) → `Run()`, which starts the `SlotResolver`. `Bootstrap.ResolveGameStateSlot` stays as the
  stateless scan+validate helper the resolver calls in its loop.
- **Surfaces** (insertion points mapped):
  - **Overlay banner** — `OverlayRenderer.DrawHealthBanner`: full-width top strip drawn first inside `if (ctx.Active)`
    (so it overlays atlas + radar), `_bPanel` backing, `_bStyle` recolored amber `(1, .85, .2, 1)` for
    `Loading`/`Searching` and red `(1, .20, .20, .95)` for `Broken`, text via the existing `_tf`. New `RenderContext`
    trailing params `HealthState Health = HealthState.Ok` + `string? HealthMessage = null` (the single Core enum; mirrors
    the nullable `CycleIndicator` pattern). `_legendRowRects` untouched by the banner branch.
  - **`/state` API** — emit `healthState = s.Health.ToString().ToLowerInvariant()` + `healthMessage = s.HealthMessage` in
    the `/state` anonymous object; add trailing optional params `HealthState Health = HealthState.Searching` +
    `string? HealthMessage = null` to the `RadarState` record and update the positional `RadarState.Empty` sentinel
    accordingly. Update-aware bits keep coming from the existing `/api/version` (no duplication).
  - **Dashboard Status panel** — a full-width `<div class='card' style='grid-column:1/-1'>` as the **first** card in the
    Settings `panel-grid`, always visible, rendered each `/state` tick: rows **Attached** (✓ when `/state` responds),
    **In zone** (`inGame`), **Reading player** (`healthState === 'ok'`), and a conditional amber/red **Health** row
    showing `healthMessage`. Mirrors the existing `renderState()` card pattern; ties the "download" link to the existing
    `checkVersion()` / `/api/version` flow.
  - **Console** — resolver uses `ConsoleTheme.WarnLine`/`Accent`; `Bootstrap`'s two `Console.Error.WriteLine` lines are
    superseded by the resolver's styled output; the message appends `UpdateChecker.ReleasesPage`.

## Data flow (per world tick)

`SlotResolver` keeps the slot fresh → `WorldTick` → `_live.Probe()` → build `ChainProbe` (stages, S4 fields,
terrainPresent, aobCandidateCount, attached, the latest `UpdateChecker.Result`) → `OffsetHealthMonitor.Evaluate(probe,
now)` → publish `_healthState`/`_healthMessage` → render thread draws the banner from `RenderContext`; `/state` serves
the fields; dashboard renders the panel. When `Probe()` reports S5 (`Ok`), the existing heavy entity/terrain/landmark walk
runs exactly as today.

## Concrete thresholds

| Name | Value | Purpose |
|------|-------|---------|
| `AOB_RETRY_CADENCE` | 1.5 s | resolver scan interval |
| `S4_STABLE_TICKS` | 3 | ticks of stable S4 before "in a zone" trusted |
| `HOLD_OFF` | 25 s | continuous `Loading` before escalating to `Broken` |
| `POST_OK_OFFLINE_WARN` | 5 min | `NotInGame` after `Ok` before soft warning |
| `RADAR_EMPTY_TICKS` | 10 (~330 ms) | `Ok` + no terrain before "deep reads stale" soft warning |
| `PROCESS_DEATH_GRACE` | 5 s | S1-fail after `Ok` before process-liveness check / re-attach |
| `AreaLevel` valid range | [0..100] inclusive | S4 zone-validity (0 = hideout) |

## Compliance

All additions are reads. `OpenProcess` stays `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` (re-attach reuses
`AttachToPoE`). Banner/message strings dodge every forbidden symbol; `scrub-strings.ps1` only touches upstream identity
tokens (not these strings, and `luther-rotmg` is not a scrubbed token). The compliance gate + `scrub-strings -SelfTest`
must pass in the final review.

## Testing

- **`OffsetHealthMonitor` (Core, the bulk):** a unit test per stress-test scenario with a fake clock —
  loading-screen-no-false-alarm; hold-off-then-escalate on a genuine break; timer-resets-on-new-zone-load; S4 stability
  filters a one-tick ghost hit; hideout `AreaLevel == 0` stays `Ok`; post-`Ok` 5-min soft warn; radar-empty soft warn;
  AOB-zero stronger wording; process-death→`Waiting`; update-aware message selection across the three `UpdateChecker`
  cases.
- **`Poe2Live.Probe()` (Core):** thin tests confirming the stage ladder and that `Rebind` clears caches; `TryResolve`
  still behaves identically (S5 path).
- **Surfaces:** compile-gated by the positional-record `Empty`/`RenderContext` sentinels; manual in-game smoke is a
  follow-up (can't fake a broken patch in CI).
- **CI:** `scripts/compliance-gate.ps1` + `scrub-strings.ps1 -SelfTest` green; full xUnit suite green.

## Out of scope (YAGNI)

- Periodic re-polling of `UpdateChecker` (one-shot at startup is fine; the banner reads whatever it has).
- A separate diagnostic HTTP server (Approach 3).
- Auto-applying offsets / any write path — forever out of scope (read-only invariant).
- Faking a broken patch in automated tests (manual smoke only).
