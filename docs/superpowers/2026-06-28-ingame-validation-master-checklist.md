# POE2GPS — In‑Game Validation Master Checklist

**Date:** 2026-06-28 · **Build under test:** main @ v0.10.0 · **Target patch:** PoE2 0.5.4

One clean, ordered pass that knocks out **every** deferred in‑game task accumulated
from v0.1 → v0.10.0 (smoke tests, offset re‑validation, and discovery probes).
It's sequenced to minimise alt‑tabbing: do everything possible at the login screen,
then a single zone visit covers core reads + all the foundational probes + every
on‑screen feature, then Atlas, then items.

## How to use this

- **Tick each box as you go.** Stop and tell me if anything FAILS — that's a real bug to fix.
- **You don't have to type any probe commands.** A one‑click launcher kit lives in **`probes\`** — double‑click
  the file that matches what you're checking (it auto‑elevates, runs, and saves the output for me to read), then
  just tell me e.g. `"core done"`. See `probes\README.txt`. Mapping: `--chain/--info/--vitals/--rarity` → **1 - core**;
  `--inventory --itemmods` → **2 - items**; `--atlas-probe/--atlas-graph` → **3 - atlas**; `--camera` → **4 - camera**;
  `--tiles` → **5 - tiles**; `--xp` → **6 - xp**; `--quest` → **7 - quest**; `--watch` → **8 - watch**;
  `--rune-dump` (metadata) → **9 - metadata**.
- The raw `dotnet run …` commands are still listed below if you'd rather type them. When a probe prints output,
  **paste it back to me** (or just say `"<name> done"` and I'll read the saved file) and I'll interpret it.
- **⭐ = critical** (gates whether the tool works at all on the live patch). Do the ⭐ items even if you're short on time.
- **🔍 = discovery probe** — only meaningful in the right situation (gaining XP, just finished a quest, etc.). Opportunistic; skip if the situation doesn't arise.
- The base probe command is always:
  `dotnet run --project src\POE2Radar.Research -c Release -- --<flag>`

---

## 0 · One‑time setup (≈2 min, before you launch)

- [ ] Pull latest `main` and confirm you're on v0.10.0 (`git log -1`, csproj `<Version>0.10.0`).
- [ ] Pre‑build the Research project once so probes launch instantly later:
      `dotnet build src\POE2Radar.Research -c Release`
- [ ] Have the published `Overlay.exe` ready (or run it from the build). You'll launch it **as Administrator**.

---

## 1 · ⭐ Attach & patch‑resilience — **at the login / character‑select screen (NOT in a zone yet)**

> This is the v0.6.0 always‑up resilience system. It shipped CI‑green but was never smoke‑tested live.
> Do this block before you load a character.

- [ ] **⭐ No‑crash without game:** with PoE2 fully closed, launch `Overlay.exe`. Expected: it prints a clean
      "Game not running / waiting" message and **does not** throw an unhandled‑exception dialog.
- [ ] **⭐ Launch at login (1a):** get PoE2 to login/character‑select. Launch `Overlay.exe`. Expected: console
      stays open, prints "Waiting for in‑game state"; dashboard Status shows **Attached ✓ · In‑zone ○**.
- [ ] **⭐ Self‑connect on zone‑in (1b):** with the overlay still running, load your character into a zone.
      Expected: within ~1–2 s terrain + dots draw, the amber "connecting" strip disappears, dashboard Status
      flips to **all three green ticks**.
- [ ] **⭐ Re‑attach on game restart (1c):** with the overlay connected, fully **close PoE2**. Confirm Status
      shows "not running". Relaunch PoE2 and load a zone. Expected: console prints **"Re‑attached to a new
      Path of Exile 2 client"**, overlay reconnects automatically (no overlay restart needed).
- [ ] **Force re‑scan:** Dashboard → Status card → **Force re‑scan**. Expected: overlay re‑resolves within ~2 s.

---

## 2 · ⭐ Core reads + offset re‑validation — **in any normal zone**

> Confirms the 0.5.4 `+0x18` chain shift fix is correct live. Visual smoke first, then alt‑tab once and run all four probes back‑to‑back.

- [ ] **⭐ Core overlay smoke:** radar dots, terrain mask, POIs, and tile landmarks all render on the in‑game map.
- [ ] **Process hygiene:** in Task Manager the overlay runs under a **random** process name; window title / tray
      do **not** say "POE2Radar"/"POE2GPS". On exit, **no stray `<random>.exe`** is left in the folder.
- [ ] **⭐ Run the four foundational probes** (alt‑tab once, run in sequence, paste me all four outputs):
      ```
      dotnet run --project src\POE2Radar.Research -c Release -- --chain
      dotnet run --project src\POE2Radar.Research -c Release -- --info
      dotnet run --project src\POE2Radar.Research -c Release -- --vitals
      dotnet run --project src\POE2Radar.Research -c Release -- --rarity
      ```
  - `--chain` PASS = **MATCH** on the LocalPlayer line, non‑zero addresses at every hop.
  - `--info` PASS = real area code (e.g. `G1_town`), sane level, non‑zero hash, your character name.
  - `--vitals` PASS = Health/Mana/ES match your in‑game orbs; every VitalStruct shows HpCur ≤ HpMax.
  - `--rarity` PASS = sensible entity count for the zone; rarities + hostility look right.
- [ ] **⭐ Camera / world‑space alignment:** stand in a monster pack. Visual check — do **HP bars sit on mobs**,
      **item labels on dropped items**, and the **guidance line land at your feet**? If yes → camera's fine,
      just tell me "world‑space overlays correct." If misaligned, run and paste:
      `dotnet run --project src\POE2Radar.Research -c Release -- --camera`

---

## 3 · Navigation & on‑screen features — **in a zone**

- [ ] **F6 / F7 routing:** press **F6** → a smoothed route draws to the nearest landmark/POI. Press **F7** → route clears.
- [ ] **Quick‑target cycler (keyboard):** **Ctrl+Alt+]** = next, **Ctrl+Alt+[** = prev (priority then distance);
      **Ctrl+Alt+1–9** = jump to slot, **Ctrl+Alt+0** = clear. The on‑screen "▸ N/M name" indicator appears and fades;
      route follows the active target. Alt‑tab out → hotkeys do **not** fire while PoE2 is unfocused.
- [ ] **Quick‑target cycler (controller, if you have a pad):** **R3** = next, **L3** = prev; **hold** R3/L3 = fast‑cycle
      (after ~400 ms, 150 ms repeat). Normal gameplay unaffected (R3 still toggles PoE2's life/mana numbers — expected overlap).
- [ ] **Menu chord:** **L3+R3** (controller) or **Ctrl+Alt+M** (keyboard) toggles the top‑left nav list without changing
      the active target. Clicking the chip also toggles it; chip reads **"POE2GPS"**.
- [ ] **Session HUD (v0.4.0):** Settings → enable **Pace**, **Zone context**, **Deaths**. HUD draws on the overlay
      and the left‑rail Session panel mirrors it. Cross two zone transitions → zones/hr increments. Die once →
      Deaths +1 (not on zone‑load flash). Press **Ctrl+Alt+R** → counters reset. Try all four **Anchor** corners.

---

## 4 · Audio, zone summary & map‑mechanic intel — **in a zone (ideally a mapped zone with a league mechanic)**

- [ ] **Audio master + 4 cues:** Settings → Audio alerts → enable master + all sub‑toggles.
      - Approach a **rare/unique monster** → tone fires once (doesn't machine‑gun; ~3 s cooldown).
      - A **unique item** drops → a different tone fires once (no tone for magic/rare).
      - Walk onto an **F6 objective** (within ~8 cells) → objective tone fires (doesn't repeat while standing on it).
      - Approach a **league mechanic** (Strongbox/Breach/Ritual/Shrine/Expedition/Essence) → mechanic tone fires once.
- [ ] **Audio Test buttons + volume + tone pickers (v0.9.1):** each per‑event **Test** button auditions its tone.
      Drag the **volume slider** (default 70) to 20 → quieter; 100 → louder. Change a per‑event **tone** (e.g. Chime→Alert)
      → distinctly different. Settings persist across an F12 dashboard reload.
- [ ] **Audio menu gate:** log out to main menu, wait 30 s → **no tones fire**; zone back in → cues resume.
- [ ] **Zone summary panel (v0.9.1, opt‑in):** Settings → enable Zone summary. A compact panel appears (default top‑right).
      Counts match the radar: **Rares / Uniques / Chests / Exits** (chests count drops as you open them). Change the
      corner setting → panel repositions.
- [ ] **Mechanic counts (v0.10.0):** in a mechanic zone the panel also shows per‑mechanic rows (e.g. "Breach: 1");
      zones with no mechanics show **no** mechanic rows. Counts decrement as you clear them.
- [ ] **Zone‑change reset:** take an exit → all counts refresh to the new zone within ~2 s (no stale bleed‑through).

---

## 5 · Custom keybinds, onboarding & settings UX — **dashboard (browser at http://localhost:7777), in a zone**

- [ ] **Keybinds rebind (v0.10.0):** Settings → Keybinds → Rebind **RouteNearest** (F6) to a new key (e.g. F4).
      In‑game F4 now routes; F6 does nothing. Rebind back to F6.
- [ ] **Keybinds reset + conflict:** rebind 2–3 actions, click **Reset to defaults** → all 9 revert. Try to bind two
      actions in the same modifier group to the same key → **409 / duplicate rejected** (no silent overwrite).
- [ ] **First‑run quick‑start (v0.10.0):** (optional — to see it fresh, back up & remove `config/settings.json`, then
      restore after.) On fresh config the dashboard shows a prominent **Quick start** card with the 3 essentials +
      hotkey cheat‑sheet + **Apply recommended setup** / **Dismiss**. Dismiss hides it (stays dismissed on reload);
      a small "Quick start" link re‑opens it.
- [ ] **Apply recommended setup:** click it → Zone summary on, ground‑item labels for **Uniques + Currency**, HP bars
      for **Rare + Unique**. Audio stays **off** (by design). Idempotent on re‑apply.
- [ ] **Settings search:** type "audio" → only audio cards show; clear → all return; nonsense string → empty/clean.
- [ ] **Collapsible cards:** click a card heading → body collapses (chevron flips). Collapse a few, F12 off/on →
      state persists. Status + Quick‑start cards stay always‑expanded.

---

## 6 · Presets, gear & director — **dashboard, in a zone**

- [ ] **Built‑in presets (v0.9.1):** Presets card → three starred built‑ins (High‑contrast, Minimal, Boss & unique hunter).
      Apply each → overlay look changes accordingly.
- [ ] **Preset round‑trip:** **Copy share‑code** → a `POE2GPS‑…` string is on the clipboard. Change a rule colour →
      paste the code → Apply → look reverts. **Download .poe2preset** → re‑import via the file picker → also reverts.
      Confirm `config/presets/backup-before-import.poe2preset` was written.
- [ ] **Local preset save/delete:** "Save current as" → name it → appears unstarred. Apply it after a colour change →
      reverts. Delete it → disappears; file removed from `config/presets/`.
- [ ] **Gear Scorer:** Settings → enable. Inspect identified items → **nonzero scores out of the box**, clickable stat‑id
      chips, **Load meta starter** repopulates weights, rarity heatmap renders. Each ranged affix shows **%‑of‑max** +
      a **T#/N** tier badge; ≥90% affixes highlight **gold**.
- [ ] **Objective Director / Catalog / Entity Atlas:** enable Director → Zone Plan card lists ranked objectives;
      auto‑routes to the top one, advances on completion, falls back to zone exit; F6 manual pick overrides until next zone.
      Director tab "Needs cataloguing" → Add (category+priority) drives the Director; Remove deletes. Entity Atlas tab →
      name a raw entity + Save (leaves "Needs a name", shows on radar); Classify adds an objective; Export/Import pack works.
- [ ] **Monolith panel (v0.1.9):** Settings → confirm **OFF by default**; toggle ON → panel appears live (no restart); OFF → gone.

---

## 7 · Atlas — **open the endgame Atlas map**

- [ ] **Atlas overlay smoke:** tracked tiles show labels + guidance lines; off‑screen arrows point toward tracked maps.
- [ ] **Dynasty‑support highlighting:** with the toggle ON, Sealed Vault / Sacred Reservoir / Derelict Mansion ring
      **purple** with "· N dynasty gems" labels + arrows + auto‑route; **Jade Isles stays Citadel‑gold**. Toggle off → all clear.
- [ ] **⭐ Run the two Atlas probes** (Atlas map open; paste me both):
      ```
      dotnet run --project src\POE2Radar.Research -c Release -- --atlas-probe
      dotnet run --project src\POE2Radar.Research -c Release -- --atlas-graph
      ```
  - `--atlas-probe` PASS = every offset prints **PASS** (no DRIFT); derived projection numbers plausible.
  - `--atlas-graph` PASS = node grid coords match the visual layout; connection counts > 0.

---

## 8 · Inventory & items — **with identified, modded items in your bags**

- [ ] **⭐ Inventory + item‑mods probe** (paste me the output):
      `dotnet run --project src\POE2Radar.Research -c Release -- --inventory --itemmods`
      PASS = inventories listed with correct grid dims; each item shows slot/rarity/identified/art; mods render as
      readable English (e.g. `IncreasedLife5 [67]` → "+67 to maximum Life").

---

## 9 · Privacy / compliance spot‑check (≈1 min)

- [ ] **No pricing, no char name:** open http://localhost:7777 → **no pricing card** anywhere. `curl http://localhost:7777/state`
      → JSON has **no non‑empty `charName`**. Spot‑check `/api/seen-pois`, `/api/entity-atlas`, `/api/labels` similarly.
- [ ] **No input emission:** play a few minutes → **no flask/skill ever fires** from the overlay; no F8 binding exists.

---

## 10 · 🔍 Discovery probes — opportunistic (only when the situation fits)

> These find *new* offsets we don't have yet. Each needs the right moment. Paste me whatever they print.

- [ ] **🔍 `--xp`** — run while actively killing mobs (it does timed passes). Looking for the **Experience** offset
      (unblocks the XP/hour HUD). Paste the "Delta report" / "<<< XP candidate" lines.
- [ ] **🔍 `--quest`** — best right after advancing/completing a quest step (run once, advance a step, run again).
      Looking for the **ServerDataStructure** quest fields for Director Part B. Paste the "Field dump" block.
      (Lead: a block at `ServerDataStructure+0x3030` flipped `0 → 0xB4000000` on completing "Trail of Corruption".
      Bounded attempt — if it doesn't surface this session, we defer it.)
- [ ] **🔍 `--tiles`** — in a non‑town area with landmarks. PASS = tile names with non‑zero grid coords.
- [ ] **🔍 `--watch`** — leave running, then take a waypoint / zone. PASS = new area code + hash logged each zone, no crash.
- [ ] **🔍 `--entity`** — handy if a mechanic audio cue *doesn't* fire: dumps raw entity metadata so we can fix the
      `MechanicPatterns` substring match.
- [ ] **🔍 League name (Sikaka graft):** check `/state` for any league field. If absent it's dormant/dead weight (fine);
      if present, confirm it matches the live league name (else the `0x21E0` offset drifted).

---

## 11 · Longer‑haul data validation (across a normal play session)

- [ ] **Campaign GPS exit hints (v0.7.0):** Settings → enable Campaign GPS. As you play Acts 1–4 + Act 6 P1/P2/P3, whenever
      the GPS banner shows a generic **"Exit"** instead of a named landmark, note the zone + the real exit landmark name.
      ~30 of 81 route steps have null `exitHint`. Tell me the zone→landmark pairs and I'll fill in
      `src\POE2Radar.Core\Game\campaign_route.json`. (Known suspects: **G3_12 (Ogham Village) → G3_14**, and the **P2/P3
      interlude ordering in Act 6**.)
- [ ] **Director catalog growth:** as you encounter named bosses / shrine types / transitions, name them in the Entity Atlas
      tab and classify the high‑value ones as Director objectives. `seen_pois.json` grows as you play.

---

## 12 · Wrap‑up

- [ ] **README badge:** if every probe PASSed on the live patch → no action (badge stays `0.5.4`). If any drifted and we
      fixed offsets → I'll bump the `supports PoE2 X.Y.Z` badge and commit.
- [ ] **Report back:** tell me which boxes FAILED (if any) and paste every probe output. I'll triage fixes and, where a
      probe surfaced a new offset (XP/quest/camera), wire it into a follow‑up release.

---

## Appendix — deferred *dev* work (NOT in‑game smoke; needs a coding session)

These are the v0.8.0 **Bucket‑2** syscall‑reduction optimizations. They were deliberately deferred because each
*rewrites the read pattern* and must be **implemented** then validated against a live entity/map view — they're not
something to "tick off" while playing. Flag me when you want a perf‑pass release and I'll implement + live‑validate each:

- `ResolveComponent` single‑pass bulk‑read (`Poe2Live.cs:1442‑1468`) — #1 syscall win (~50–140 string allocs/entity removed).
- `ReadReaction` value‑cache (`Poe2Live.cs:728‑737`) — ~12k RPM/s saved on a 400‑entity map; needs a conversion‑mob check.
- `TryReadMapElement` single bulk‑read (`Poe2Live.cs:1057‑1065`).
- `ReadChestOpened` one‑way cache (`Poe2Live.cs:1375‑1380`).
- `PlayerVitals` world‑thread split → expose `EnsureVitalOffsets()` (`RadarApp.cs:1157`).
- `ReadMods` bulk‑read with HashSet dedup (`Poe2Live.cs:614‑628`).
- `Entities()` caller‑owned reused list (`Poe2Live.cs:415`) — validate no torn snapshots.
- Terrain raw‑buffer `ArrayPool` (`Poe2Live.cs:958`).

Source: `docs/audit-2026-06-27-perf.md` (Bucket 2).
