# POE2GPS — Design Spec

- **Date:** 2026-06-21
- **Status:** Approved 2026-06-21
- **Author:** Ryan Duke (with Claude Code)
- **Topic:** A read-only PoE2 GPS/navigation overlay derived from Sikaka/POE2Radar + NattKh/POE2Radar

---

## 1. Summary

POE2GPS is a **read-only navigation overlay** for Path of Exile 2. It reads game state from
process memory and draws a radar/GPS + atlas route-planning overlay. It is constructed as a
**fork of [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar)** with the keystroke-sending
auto-flask removed and the process-randomization / anti-detection subsystem from
[NattKh/POE2Radar](https://github.com/NattKh/POE2Radar) grafted on.

The project produces **three deliverables**:

1. **POE2GPS** — the maintained "community-safe" fork (this repo).
2. **Upstream PR #1 to Sikaka** — the process-randomization module, offered as an opt-in feature.
3. **Upstream PR #2 to Sikaka** — NattKh's F1–F5 byte-patch cheats, offered as a friendly
   "take it or leave it" optional feature set (scoped deliberately; see §11).

The defining property of POE2GPS is enforced, not asserted: a **compliance gate** (§9) fails the
build if any input-emission or process-write API ever appears in the shipped source.

---

## 2. Goal & non-negotiable invariants

POE2GPS does three things **never**, enforced by CI (§9):

- **I1 — Never emits input to the game.** No `SendInput`, `keybd_event`, `mouse_event`,
  `PostMessage`/`SendMessage`-to-game, `SetCursorPos`-for-clicks, `SendKeys`, or input-simulator
  libraries.
- **I2 — Never writes to / injects into the game process.** No `WriteProcessMemory`,
  `NtWriteVirtualMemory`, `VirtualAllocEx`, `VirtualProtectEx`, `CreateRemoteThread`,
  `QueueUserAPC`, `SetWindowsHookEx`-injection, or mapped-section injection. The game is opened
  with `PROCESS_VM_READ | PROCESS_QUERY_(LIMITED_)INFORMATION` only — never `PROCESS_VM_WRITE`
  or `PROCESS_VM_OPERATION`.
- **I3 — Never makes external automation/economy calls.** No MCP server, no poe.ninja / poe2scout
  network calls.

What POE2GPS **does**: external, read-only memory reading (`OpenProcess` read-only +
`ReadProcessMemory`/`NtReadVirtualMemory`/`VirtualQueryEx`), Direct2D overlay rendering, in-zone
and atlas route **drawing** (never driving input), and a localhost-only dashboard.

### Honest compliance posture

Read-only external memory reading is **tolerated, not blessed** by GGG. The README will state this
plainly rather than overclaim "100% safe." Removing all input emission and all process writes puts
POE2GPS in the lowest-risk category that GGG has been de-facto agnostic toward for years. The
anti-detection / randomization subsystem (§8) is retained at the user's explicit direction; the
README documents what it does without euphemism.

---

## 3. Source material & verified findings

Both repos are C#/.NET, MIT-licensed, structured as `src/{POE2Radar.Core, POE2Radar.Overlay,
POE2Radar.Research}`. The findings below were verified by reading the actual cloned source
(`.reference/sikaka`, `.reference/nattkh`), not the READMEs.

### 3.1 NattKh forked an *older, leaner* Sikaka

NattKh predates large parts of current Sikaka. **Current Sikaka is a superset.** NattKh **lacks**:
the atlas projection/graph subsystem (`Poe2Atlas.cs`, `Overlay/Navigation/RouteTracker`,
`BackgroundReplanner`, `PathPlanner`/`PathSmoother`), poe.ninja pricing, `EntityNameResolver`,
`ItemModTranslator`, `IconLibrary`, `world_areas.json`, and the two-threaded render/world loop.
NattKh's `Research/Program.cs` is 860 lines vs Sikaka's 7130.

**Consequence:** the atlas overlay + route planning the user wants to keep is **Sikaka-native**.
Approach A (fork Sikaka) keeps it for free; the randomization graft lands on a *more advanced*
codebase than NattKh's, so it is a manual re-application, not a cherry-pick.

### 3.2 Sikaka is already injection-free and read-only

Sikaka's `NativeMethods.cs` declares **only** read/query APIs (`OpenProcess`, `ReadProcessMemory`
[declared but unused — reads go via `NtReadVirtualMemory`], `NtReadVirtualMemory`, `VirtualQueryEx`,
module enumeration). `OpenProcess` requests `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`
only (`ProcessHandle.cs:66-69`). A whole-repo sweep found **zero** `WriteProcessMemory` /
`VirtualProtectEx` / `VirtualAllocEx` / `CreateRemoteThread`.

The **only** input-emission path in Sikaka is auto-flask: `Input/SendInputNative.cs` (`SendInput`
at `:41,58`), called from `RadarApp.cs:1428,1433`. **Remove auto-flask and Sikaka passes a strict
read-only gate cleanly.**

### 3.3 NattKh is NOT read-only — it has two memory-write systems

- **`CheatManager` (F1–F5, LIVE/wired):** `src/POE2Radar.Core/Cheats/CheatManager.cs` +
  `CheatDefinition.cs`. Five AOB-pattern byte patches — `NoAtlasFog`(F1), `RevealMap`(F2),
  `InfiniteZoom`(F3), `EnemyHealthBars`(F4), `PlayerLightRadius`(F5, slider). Applies via
  `VirtualProtectEx(PAGE_EXECUTE_READWRITE) → WriteProcessMemory → restore`. Self-contained; wired
  by ~15 lines in `RadarApp.cs:72-79,508-518` + a `SettingsForm` tab. Depends only on `AobScanner`
  (already in Sikaka).
- **`GameVisualTweaks` (DEAD/unwired):** `src/POE2Radar.Core/Cheats/GameVisualTweaks.cs`. ~60
  toggles writing entity-component memory (phase-through, freeze/friendly-swap monsters, unlock
  chests, make-all-boss, hide life bars, etc.). **`new GameVisualTweaks(...)` appears nowhere** —
  never instantiated, no hotkey. Dead code, far more egregious than F1–F5.

NattKh also added `WriteProcessMemory` + `VirtualProtectEx` and the `PROCESS_VM_WRITE`(0x0020) /
`PROCESS_VM_OPERATION`(0x0008) flags to `NativeMethods.cs:8-9,28,32`.

Both repos' `CLAUDE.md` claim "stay external, never inject" — **true for Sikaka, false for NattKh**
(the `Cheats/` layer contradicts it). The gate (§9) is exactly what catches this drift.

### 3.4 NattKh process-randomization mechanism (verified)

Four parts, spread across four files:

1. **Random-named hardlink self-relaunch** — `Program.cs`: `GenName()` (pronounceable
   consonant/vowel, `:99-115`) → `CreateHardLink` (kernel32, `:117-118`) →
   `Process.Start(target, "--launched")` → original returns 0. On exit, deletes every `*.exe` in
   the dir except `Overlay` and self (`:80-95`). Guard at `:8-35`.
2. **"String stripping" — NOT a real scrub.** There is no ILStrip/post-build step. Identity is
   suppressed only by a generic `<AssemblyName>Overlay</AssemblyName>` (csproj `:5`) plus running
   under the random hardlink name and a random `Console.Title`. `publish.ps1` confirmed clean of
   any string-removal step.
3. **Window randomization** — `OverlayWindow.cs`: random class name `wc_` + `GetRandomFileName`
   (`:74`), random title `wt_` + `GetRandomFileName` (`:107`), `WS_EX_TOOLWINDOW` (off-taskbar).
4. **Character-name hiding** — `RadarApp.cs:141,160-189` reads `PlayerName` for the API but
   deliberately omits it from the `RenderContext`, so it never reaches the on-screen HUD. (NattKh's
   checked-out renderer is mid-refactor and references `ctx.CharName`/`AreaName` fields that don't
   exist on its `RenderContext` — it would not compile as-is. The `RenderContext` omission is the
   authoritative mechanism.)

---

## 4. Approach

**Chosen: Approach A — Fork Sikaka, subtract auto-flask, graft randomization as an isolated
module.** Verification validates this decisively (§3.1, §3.2): Sikaka is the more advanced base,
already injection-free, and natively carries the atlas/route features being kept. The randomization
graft is the only forward-port, and it touches few files.

Rejected: **B (fork NattKh)** — based on a stale, leaner codebase; pulling Sikaka's superset
updates would be the hard merge direction; lacks the atlas features. **C (clean-room rewrite)** —
loses the git-merge path to Sikaka, forcing manual offset chasing every game patch.

---

## 5. Repo & branch topology

- `POE2GPS` = a **fork of Sikaka/POE2Radar** (shares Sikaka's commit history) with remote
  `upstream` → `https://github.com/Sikaka/POE2Radar`.
- `main` = Sikaka base **−** auto-flask **−** poe.ninja pricing **−** (NattKh extras not taken)
  **+** randomization module **+** trimmed dashboard.
- Feature branches:
  - `feature/process-randomization` — the randomization module; **also opened as Upstream PR #1
    to Sikaka**.
  - `feature/byte-patch-offering` — NattKh's F1–F5 cheats rebased onto Sikaka; **opened as Upstream
    PR #2 to Sikaka only. Never merged into POE2GPS `main`.**

### 5.1 Repo bootstrap procedure (for the implementation plan)

1. Establish POE2GPS on **Sikaka's history** (fork on GitHub, or push Sikaka's history into a new
   `POE2GPS` repo). Add `upstream` → Sikaka.
2. Add this `docs/` tree on top as a normal commit (zero conflict — Sikaka doesn't use this path).
3. The two clones under `.reference/` are inspection-only and are **gitignored** (never committed).

---

## 6. Scope — keep / remove / port inventory

| Item | Action | Source | Effort / risk | Notes |
|---|---|---|---|---|
| Core read-only radar (entities, terrain, POIs, landmarks, watched labels, threat coloring) | **keep** | Sikaka | — | Already read-only (§3.2). |
| Atlas overlay + route planning | **keep** | Sikaka | — | Draw-only A*; `PathPlanner` "never drives input". Sikaka-native (§3.1). |
| Web dashboard (localhost:7777) | **keep, trim** | Sikaka | low | Remove only the pricing card. |
| Auto-flask (keystroke subsystem) | **remove** | Sikaka | medium | `SendInputNative.cs` deletes whole; threads through `RadarApp.cs`/`RadarSettings.cs`/`ApiServer.cs`/`DashboardHtml.cs`. |
| poe.ninja pricing | **remove (network) + trim (value chips)** | Sikaka | high | `PriceBook.cs` deletes whole; consumers woven across `RadarApp.cs` (BuildItemLabels, runeforge/ritual/monolith/loot-tag overlays). Keep name labels, drop value numbers (§6.1). |
| Process randomization / anti-detection | **port** | NattKh | medium | 4-part graft (§8). No new files except the optional real string-scrub. |
| F1–F5 byte-patch cheats | **exclude from POE2GPS; offer upstream** | NattKh | — | Upstream PR #2 only (§11). |
| `GameVisualTweaks` (dead cheat system) | **delete; exclude from upstream PR** | NattKh | — | Dead, egregious (§3.3, §11). |
| MCP server | **exclude** | NattKh | — | Not taken (Approach A starts from Sikaka). |
| AutoRule engine + `autohotkey_research.md` | **exclude** | NattKh | — | Not taken; Sikaka's base has no rule engine. |

### 6.1 Pricing-removal decision

`PriceBook.cs` (the sole poe.ninja/poe2scout caller) is deleted to satisfy **I3**. Its consumers
in `RadarApp.cs` (`BuildItemLabels`, `UpdateRuneforge`, `UpdateRitualRewards`, `UpdateLootTags`,
`UpdateMonoliths`) are **trimmed, not deleted**: keep the item/reward **name labels**, drop the
**value chips**. Navigation and loot awareness are unaffected; only economy pricing disappears.
*(Confirmed 2026-06-21 — §15.)*

---

## 7. Component architecture (the three .NET projects)

- **POE2Radar.Core** — memory read, `Poe2Offsets`, `Poe2Live`, `Poe2Atlas`, `Pathfinding/`. Kept
  as-merge-clean-as-possible with Sikaka (touch minimally). No write APIs ever added (would fail
  the gate).
- **POE2Radar.Overlay** — render loop + dashboard host + entry point. Here we **delete** auto-flask
  (`Input/SendInputNative.cs` + wiring) and `PriceBook` + value-chip consumers, **trim** the
  dashboard pricing card, and **add** the randomization module (§8).
- **POE2Radar.Research** — dev-only offset tooling. Never linked into the `.exe`; **excluded** from
  the compliance gate scan (contains no write/input symbols, verified).

---

## 8. Process-randomization module (graft onto Sikaka)

Re-implement NattKh's four-part mechanism (§3.4) onto Sikaka's base. Keep it as isolated as
possible so it doubles as Upstream PR #1 and stays low-conflict on merges.

1. **Hardlink self-relaunch** — port `Program.cs` `GenName` + `CreateHardLink` + `--launched`
   guard + on-exit `*.exe` cleanup. Sikaka's `Program.cs` is ~32 lines (plain attach); wrap its
   body in the relaunch guard (~40 lines added). Low risk.
2. **Window randomization** — random class name + title in Sikaka's `OverlayWindow`. Two string
   substitutions. Low risk.
3. **Generic assembly name** — set `<AssemblyName>Overlay</AssemblyName>` in the Overlay csproj.
   Trivial.
4. **Character-name hiding** — locate where Sikaka's (more elaborate, config-driven) HUD prints the
   player name and remove it from the render path. The medium-risk part; needs care.
5. **Real string-scrub** — add a genuine post-build step that scrubs identifying ASCII/UTF-16
   strings from the published binary. *Not present in NattKh.* **Adopted** (§15) so "full
   anti-detection" is more than naming hygiene; the randomization unit tests assert the scrubbed
   output carries no `POE2`/`PoE`/`Radar` identity strings.

The module exposes a single startup hook so its blast radius is one call site.

---

## 9. Compliance gate — the "community-safe" guarantee

A CI step (and a pre-merge local script) that **fails the build** if any forbidden symbol appears
in shipped source. This both proves the safety claim and **auto-catches accidental re-introduction
of auto-flask/byte-patch when merging Sikaka updates**.

### 9.1 Forbidden buckets (hard-fail)

- **Process-write:** `WriteProcessMemory`, `NtWriteVirtualMemory`, `ZwWriteVirtualMemory`,
  `VirtualAllocEx`, `VirtualProtectEx`, `VirtualFreeEx`, `NtProtectVirtualMemory`,
  `CreateRemoteThread(Ex)`, `RtlCreateUserThread`, `QueueUserAPC`, `NtQueueApcThread`,
  `SetWindowsHookEx(W)`, `NtMapViewOfSection`, `ZwMapViewOfSection`. Plus the access flags
  `PROCESS_VM_WRITE`, `PROCESS_VM_OPERATION`, `PROCESS_CREATE_THREAD`. Plus any file under
  `**/Cheats/**` by path.
- **Input-emission:** `SendInput`, `keybd_event`, `mouse_event`, `PostMessage(A/W)`,
  `SendMessage(A/W)`, `SendMessageTimeout`, `SendNotifyMessage`, `SetCursorPos`, `BlockInput`,
  `SetKeyboardState`, `SendKeys.Send(Wait)`, `InputSimulator`/`IKeyboardSimulator`/
  `IMouseSimulator`. For POE2GPS specifically, `src/**/Input/SendInputNative.cs` and any
  `*.Tap(...)` call site are forbidden outright.

### 9.2 OpenProcess access-mask check

`OpenProcess` stays allowed, but the gate inspects `dwDesiredAccess` and **fails** if it references
`PROCESS_VM_WRITE` or `PROCESS_VM_OPERATION`. Sikaka passes (read-only); the check prevents a future
merge from silently widening access.

### 9.3 Allowlist (must NOT fail)

`OpenProcess` (read-only mask), `ReadProcessMemory`, `NtReadVirtualMemory`, `VirtualQueryEx`,
`EnumProcessModules(Ex)`, `GetModuleInformation`/`GetModuleFileNameEx`, `CloseHandle`,
`GetForegroundWindow`, `GetWindowRect`/`GetClientRect`, `GetWindowThreadProcessId`. Own-overlay-
window ops are benign: `ShowWindow`/`SetWindowPos`/`UpdateLayeredWindow`, and the single
`SetForegroundWindow(_hwnd)` tray-dismiss idiom (own window, not the game). `HttpListener`
(localhost dashboard) is unrelated to game I/O. Benign exceptions live in a checked-in allowlist
file keyed by `file:line` + justification, so an approved hit doesn't block CI but any **new**
occurrence does.

### 9.4 Scope & mechanism

Scan `src/` `*.cs` only; exclude `**/POE2Radar.Research/**`, `**/bin/**`, `**/obj/**`. Use ripgrep
with word boundaries / `-P` look-around so `ReadProcessMemory` never matches the write regex and
managed `File.WriteAllText`/`Console.WriteLine` are never flagged. Example:

```
rg -nP --type cs '(?<![A-Za-z_])(SendInput|keybd_event|mouse_event|WriteProcessMemory|NtWriteVirtualMemory|VirtualAllocEx|VirtualProtectEx|CreateRemoteThread|QueueUserAPC|SetWindowsHookEx|PostMessage|SendMessage|SetCursorPos)(?![A-Za-z_])' src/ \
  --glob '!**/POE2Radar.Research/**' --glob '!**/bin/**' --glob '!**/obj/**' \
  && echo 'FORBIDDEN SYMBOL FOUND' && exit 1
```

Wired into a GitHub Actions workflow on push/PR, and runnable locally (`scripts/compliance-gate.ps1`)
as part of the upstream-merge ritual (§12).

---

## 10. Data flow (one-directional, provably read-only)

```
PoE2 process ──NtReadVirtualMemory──▶ Core (live state) ──▶ Overlay renderer ──▶ screen
                                              └────────────▶ Dashboard (localhost:7777, read-only view)
```

No arrow ever points back into the game. Route planning produces **drawn lines only**; it never
drives input. That is the entire compliance story in one diagram, and §9 enforces it mechanically.

---

## 11. The three deliverables

1. **POE2GPS repo** — §5/§6/§7/§8 applied; compliance gate green; README leading with the
   transparency story.
2. **Upstream PR #1 → Sikaka: process randomization.** The §8 module, extracted as an opt-in
   feature (disabled or behind a flag by default, maintainer's choice). Friendly, self-contained.
3. **Upstream PR #2 → Sikaka: F1–F5 byte-patch.** Scoped to **`CheatManager` + `CheatDefinition`
   (F1–F5) only**, depending on Sikaka's existing `AobScanner`, plus the two write P/Invokes and
   ~15 lines of hotkey wiring and an optional `SettingsForm` tab. **Explicitly excludes the dead
   `GameVisualTweaks` system** (unlock-chests / phase-through / freeze-monsters / make-all-boss —
   dead, egregious, and references offset fields Sikaka may not have). The PR is clearly labeled
   with the ToS risk ("this modifies the running game process") and offered as opt-in/disabled-by-
   default so Sikaka can make an informed call or decline. **None of this enters POE2GPS.**

---

## 12. Maintenance / upstream-merge workflow

`git fetch upstream && git merge upstream/main`. Because auto-flask is fully deleted, a Sikaka
change to auto-flask code surfaces as a conflict on deleted files — resolve by keeping the deletion;
the §9 gate then confirms nothing crept back. A short `docs/upstream-merge.md` records the exact
strip-list (auto-flask call sites, `RadarState` flask fields, pricing consumers) so re-asserting the
invariants after a big merge is a checklist, not archaeology. Highest-churn conflict surfaces:
`RadarApp.cs` (large, central), `ApiServer.cs`/`DashboardHtml.cs` (settings whitelist + cards).

---

## 13. Testing & verification strategy

- **Static compliance gate (§9)** — fully automated; the centerpiece and the safety proof.
- **Build + headless smoke** — solution builds; overlay initializes and the dashboard serves
  without a game attached (graceful "PoE2 not running" exit path already returns code 1).
- **Randomization unit tests** — `GenName` produces valid/varied names; hardlink creates+cleans in
  a temp dir; published output contains no `POE2`/`PoE`/`Radar` identity strings (asserts the
  optional scrub if adopted); `RenderContext` carries no player name.
- **Manual in-game checklist** — radar, atlas, and route lines render correctly against a live
  client. Cannot be automated (requires a running PoE2); documented as a release checklist.

Honest limit: full functional verification needs a live PoE2 client and current offsets; the
automated layer cannot cover the in-game render path.

---

## 14. Licensing & attribution

Stays **MIT**. `NOTICE`/README credit Sikaka, NattKh, GameHelper2, and landmark/database
contributors. README leads with: read-only, zero input emission, zero process writes, here is the
CI proof — and states plainly that memory reading is ToS-gray (tolerated, not blessed).

---

## 15. Resolved decisions (confirmed 2026-06-21)

1. **Pricing trim (§6.1):** ✅ Keep item/reward **name labels**, drop the value chips. The loot/
   reward overlays stay; only poe.ninja value numbers and the network layer go.
2. **Real string-scrub (§8.5):** ✅ Adopt a genuine post-build binary string-scrub — "full
   anti-detection" should be more than a generic assembly name.
3. **Upstream PR #2 scope (§11):** ✅ F1–F5 (`CheatManager`/`CheatDefinition`) only; **exclude** the
   dead `GameVisualTweaks` system entirely.
4. **Randomization default in PR #1:** ✅ Offer to Sikaka **opt-in / flagged-off by default**.

---

## 16. Risks

- **Offset breakage every game patch** — mitigated by the `upstream` merge path to Sikaka.
- **Sikaka may decline either PR** — fine; POE2GPS is independent and self-maintained.
- **Merge churn on hot files** (`RadarApp.cs` et al.) — mitigated by the strip-list doc + §9 gate.
- **Memory-reading is ToS-gray** — disclosed in README; not overclaimed.
- **Anti-detection optics** — randomization can read as cheatware; retained at user's explicit
  direction and documented honestly.
- **"Fork on a superset" graft** — randomization lands on code that differs from NattKh's; requires
  careful manual re-application, especially the char-name omission in Sikaka's richer renderer.
```
