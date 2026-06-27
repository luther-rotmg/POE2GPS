# In-Game Validation Session — 2026-06-26

One sitting that (a) smoke-tests the just-shipped **v0.6.0 patch-resilience**, (b) closes the **open Camera-on-0.5.4** risk, and (c) runs the **`--xp`** and **`--quest`** discovery probes that unblock XP/hour and Director Part B. Paste the marked output back and I'll wire the features / confirm the fixes.

## 0. Setup (once)

- **Run PoE2** and get a character **loaded into a zone** (not at login/character-select, unless a step says so).
- Open a terminal **as Administrator** (probes + overlay need `OpenProcess` read rights). Easiest: Start → type `terminal` → right-click → *Run as administrator* → `cd C:\Users\minec\Documents\Projects\POE2GPS`.
- The probes run with: `dotnet run --project src\POE2Radar.Research -c Release -- <flag>` (the project is already built, so this is quiet — only the probe output prints).
- For the overlay smoke you need a **v0.6.0** build: either your existing install if it's already v0.6.0, or download `POE2GPS-v0.6.0-win-x64.zip` from https://github.com/luther-rotmg/POE2GPS/releases/tag/v0.6.0 and run `Overlay.exe` **as Administrator**.

---

## 1. v0.6.0 patch-resilience smoke ⭐ (the new feature — do this first)

This is the part with no automated test (CI can't fake a broken patch), so it's the highest-value check.

**1a — Launch before you're in a zone (the old papercut).**
- Get PoE2 to the **login / character-select** screen (not in a zone yet).
- Launch `Overlay.exe`.
- ✅ **Expected:** it does **not** flash-and-close. The console stays open and prints a waiting line (`Waiting for in-game state — load into a zone.`). Open the dashboard (**F12** or http://localhost:7777) → **Status** card (top of the Settings tab) shows **Attached ✓ · In a zone ○ · Reading your character ○**, and the overlay shows an amber **"Connecting to Path of Exile 2…"** strip while PoE2 is focused.
- ❌ **Old behavior (regression if you see it):** console prints "no slot resolved… make sure you're loaded into a zone" and the window closes.

**1b — Zone in → it self-connects.**
- Load your character into a zone.
- ✅ **Expected:** within ~1–2 s the overlay connects on its own — terrain + dots draw, the amber strip disappears, and the dashboard **Status** flips to **Attached ✓ · In a zone ✓ · Reading your character ✓**.

**1c — Survive a game restart (re-attach).**
- With the overlay still running, **fully close PoE2.**
- ✅ **Expected:** Status shows **"Path of Exile 2 is not running"** (overlay keeps running, draws nothing).
- **Relaunch PoE2** and load into a zone.
- ✅ **Expected:** the overlay **re-attaches by itself** — the console prints `Re-attached to a new Path of Exile 2 client — re-resolving…`, then it reconnects when you're in a zone. No overlay restart needed.

**Paste back:** just tell me pass/fail on 1a / 1b / 1c, and copy any odd console lines. (The red **"out of date"** banner only fires on a genuine offset break, so we can't trigger it today — that's expected.)

---

## 2. Camera verification on 0.5.4 (the open risk)

The world-space overlays (mob HP bars, ground-item labels, the on-ground guidance path) use the camera matrix at `InGameState+0x368`, which was **never re-confirmed on 0.5.4** (a probe bug masked it earlier). Quick visual check first:

- With the overlay running, stand in a **monster pack** with the in-game map open.
- ✅ **If the HP bars sit on the mobs, item labels sit on dropped items, and the guidance line lands at your feet** → camera is fine on 0.5.4, **skip the probe**, just tell me "world-space overlays correct."
- ❌ **If those are missing or float in the wrong place**, run the probe (stand still inside a pack):

```
dotnet run --project src\POE2Radar.Research -c Release -- --camera
```

**Paste back:** the `Camera(*+0x368)` + `Camera.Zoom` lines and **every** `matrix@+0x…` candidate line (the real one has `player≈center`, a high `onScreen=N/M`, and a healthy `spreadX`). I'll confirm or re-point the offset.

---

## 3. `--xp` probe — unblocks XP/hour

Discovers the player Experience offset (scans the Player component, flags the value that **increases** as you kill things).

```
dotnet run --project src\POE2Radar.Research -c Release -- --xp
```

- It runs **3 passes with 5-second pauses**. **Kill a mob or two during each pause** (the prompt `[sleeping 5s — kill a mob or two now]` will tell you when).
- ✅ First sanity line should read `Level @ +0x204 = <your level>` (1–100). If it's 0 the chain didn't resolve — make sure you're in a zone.

**Paste back:** the whole **`── Delta report ──`** block — I need the line(s) tagged `<<< XP candidate (increased)`. That offset becomes `Poe2.PlayerComponent.Experience`, and I'll build the XP/hour + time-to-level HUD off it.

---

## 4. `--quest` probe — unblocks Director Part B (quest-aware cross-zone)

Exploratory dump of the ServerDataStructure (where quest-completion state most likely lives, next to the inventories).

```
dotnet run --project src\POE2Radar.Research -c Release -- --quest
```

- Best run **right after you complete or advance a quest step** (so a flag/counter has just changed — if you can run it once, advance a quest, and run it again, the diff is gold).

**Paste back:** the full **`── Field dump ──`** block (the qword list with `PTR`/`val`/`<<< std::vector?` tags). I'll eyeball it for quest-state candidates and, if a second post-quest-step dump is available, diff them to pin the field.

---

## 5. Quick regression smoke of the recent releases (optional, ~2 min)

Since v0.4.0/v0.5.0/v0.5.2 were all shipped without an in-game test, a fast pass while you're here:

- **v0.4.0 Session HUD:** Settings → enable Pace/Zone/Deaths toggles → confirm the HUD draws on the overlay and the left-rail Session panel mirrors it. Press **Ctrl+Alt+R** → counters reset.
- **v0.5.0 Director:** enable "Objective Director" → the **Zone Plan** card lists ranked objectives; the Entity-Atlas tab pre-fills tier/category guesses on notable uncatalogued rows.
- **v0.5.2 Target cycling:** with a controller, **R3** = next target / **L3** = prev (radar-menu order by default), **hold** R3/L3 = fast-cycle, **L3+R3** = toggle nav menu. Keyboard `Ctrl+Alt+]`/`[` same.

**Paste back:** anything that looks wrong; otherwise "regression smoke clean."

---

### After you paste results

- **Camera ok** → I mark the 0.5.4 open item closed.
- **XP candidate found** → I build the XP/hour feature (brainstorm → spec → SDD).
- **Quest dump** → I analyze it and design Director Part B from the real structure.
- **v0.6.0 1a–1c pass** → patch-resilience is verified; if 1c (re-attach) is flaky I'll know exactly where to look.

*(Note for me: the `--quest` source comment still says `AreaInstance+0x580` but the code correctly uses `ServerDataPtr=0x598` for 0.5.4 — cosmetic, output confirms the right address.)*
