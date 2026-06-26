# Island Rumours — Resume Packet (2026-06-25)

Single source of truth to resume the **Expedition "Island Rumours"** feature. Read this first.

## ⏸ STATUS: SHELVED (2026-06-25, Ryan's call)

Parked after the v4 discovery dumps. **What's CONFIRMED:** the offered rumour names are plain UTF-16
text on **depth-4 slot widgets** in the panel; v4's wide net reached some of them (e.g. "Wild roaming
free" reliably at a slot's `body→ptr→+0xF0`). **Why shelved:** across 7 v4 dumps the bounded net captured
each screen's offered names only **~1 in 3 times**, scattered at inconsistent depths/offsets, often clipped
by the 0x100 sample window or masked by font runs — and the dump format doesn't record which body pointer
feeds each name. So **no dependable all-names read recipe** came out of offline captures, and more dumps
wouldn't change that (same gaps every time). It's a QoL extra that cost far more than its worth; the real
win this session was the v0.5.1 0.5.4 offset hotfix.

**To resume (only if a faster way in appears):** do NOT mail more dumps. Either (a) **live interactive RE**
— someone at an Island Rumours screen while I iterate a Research probe in real time (fast convergence), or
(b) build a best-effort reader on the depth-4-slot pattern and test it live in the overlay (fast yes/no,
low-med confidence). Dumps are saved at `C:\Users\minec\rumourdumps2\v4-dump1..7-*.txt` + the `xref.py`
cross-reference. Tier table reconciliation (the user's current Dracorath sheet vs our embedded one) is a
separate, still-valid Task 1 correction if resumed.

## What the feature is

Read the PoE2 Expedition "Island Rumours" the game is offering, rank them by a desirability
**tier** table (Dracorath cheat-sheet, S+→F — **tiers only, never prices/market data** for
compliance), and show a ranked panel on the overlay + dashboard that highlights the best pick.

## Where we are

- **Branch:** `feat/island-rumours` — now merged with the 0.5.4 offset fix (`main @ ed2ff7e`).
  Builds **0 errors**, **157 tests pass**. Local only (not pushed; commits are in the ledger).
- **Spec:** `docs/superpowers/specs/2026-06-24-island-rumours-design.md` (commit `32b8250`).
- **Plan:** `docs/superpowers/plans/2026-06-24-island-rumours.md` (5 tasks, TDD; commit `1b1b8c2`).
- **Ledger:** `.superpowers/sdd/progress.md`.
- **Task 1 — tier table + pure logic:** ✅ DONE + review-approved (`380896e`). `Core/Game/IslandRumours.cs`
  + `island_rumours.json` (25 entries, no prices) + `IslandRumoursTests.cs` (23 tests). Pure
  `MatchLabel(raw)→RumourEntry?`, `RankOffered(labels)→RankedRumour[]`, `TierRank(s)→int`
  (S+=6…F=0, unknown=-1). One Minor (IR-M1): two `Assert.Equal(1, count)` → should be `Assert.Single`
  at `IslandRumoursTests.cs:563/584` — fix during the final review.
- **Task 2 — read layer:** committed (`9e671fb`) but **BLOCKED** on offered-vs-catalog discrimination
  (below). Added `Poe2.IslandRumour` offsets (`TextStructPtr=0x138`, `Str1=0x20`, `Str2=0x50`) +
  `Poe2Live.ReadOfferedRumours(inGameState)` — bounded UI-tree walk, magic-guard fast-reject, the text
  recipe, matches via `IslandRumours.MatchLabel`. Walks **unconditionally** (no visible filter — the
  spec's visible-chain filter was proven empirically wrong).
- **Tasks 3 (wiring/config), 4 (display panel + dashboard), 5 (integration sweep + final review):** PAUSED.

## The blocker (why we paused)

We can READ the rumour text perfectly, but **the game keeps the ENTIRE rumour catalog in memory**, not
just the 2–3 offered. We can't yet isolate which ones are actually OFFERED:

- The magic-guard (`+0x138` text widgets) returns **both** offered AND catalog labels — e.g.
  "Warm but risky" (6 `+0x138` widgets) and "It's dry at least" (1) are **catalog flavour text, not
  offered**, yet are `+0x138` widgets.
- The spec's **"visible-parent-chain filter" is WRONG**: the offered "Endless Cliffs" element
  (`0x30FC6067230`) parent chain is `depth5 leaf visible=True → depth4 wrapper 0x30FE0D8CF30
  visible=False → depth3 container 0x30FA6BAB910 (children=786) visible=True`. The offered element's
  OWN depth-4 wrapper is `visible=False`, so a "whole chain visible" filter would DISCARD the offered.
- **Only lead:** the depth-3 container's `+0x190` holds exactly **3 UTF-16 area-ID strings = the
  offered destinations**, Caesar-(−3) encoded (`UHFRUGV`→`RECORDS`, `IXWXUH`→`FUTURE`). Needs a decode
  + a label→area map.

## The read recipe (validated on the 0.5.x dump — RE-VALIDATE on 0.5.4)

- UiElement body **`+0x138`** → text-struct.
- **Magic-guard** `{0x91,0x9C,0x9F,0xFF,0x01,0x01,0x00,0x00}` at struct **`+0x10`** (fast-reject).
- `textBuf = struct+0x18`; display string = null-terminated UTF-16 at **`textBuf+0x08`** UNLESS it
  begins with **"Fontin"** (font override) → the next UTF-16 run (~`struct+0x50`).
- UiRoot = `InGameState+0x2F0`; UiElement `Self +0x08`, `Children +0x10`, `Flags +0x180` (visible = bit `0x0B`).

## 0.5.4 implications (the game patched mid-build)

- The recipe + `Poe2.IslandRumour` offsets were derived from a **0.5.x** dump. 0.5.4 may have shifted
  the UI element internal layout (`+0x138`, the magic-guard bytes, the str offsets). The AreaInstance
  +0x18 shift does NOT affect the rumour reader (it uses UiRoot/UiElement, which still resolve on 0.5.4 —
  atlas/map/pathlines all work).
- So the NEW dumps do **double duty**: (1) re-validate/re-derive the recipe on 0.5.4, (2) the
  offered-vs-catalog discrimination.
- **Fresh 0.5.4 diag build is LIVE: `v0.5.1-rumourdiag2`** (branch `diag/rumour-0.5.4`, built off the
  `v0.5.1-rumourdiag` tag = the config-write Dump tool + merged the 0.5.4 fix; EXCLUDES the rejected
  browser-download commit `8ca74ee`). Working radar + the "Dump Rumours" button (Settings → Diagnostics,
  writes a `.txt.gz` to `config/`). Marked **prerelease**; **v0.5.1 stays Latest** (normal users + the
  update-checker aren't pointed at it). This is the build the user's user uses for the new 0.5.4 dumps.
  Download: https://github.com/luther-rotmg/POE2GPS/releases/tag/v0.5.1-rumourdiag2

## Mine findings (2026-06-25) — 7 dumps EXHAUSTIVELY analysed: insufficient, but recipe + structure confirmed

A 3-lens multi-agent mine of all 7 dumps (high confidence, exhaustive) concluded:
- **The offered signal is NOT in these shallow dumps.** No body/deref field == offered-count in any
  dump-correlated way. The old `+0x190` Caesar lead is DEAD — the area-IDs (FUTURE/RECORDS at body
  `+0x1A4/+0x1B2` of the depth-3 catalog container) are STATIC across all 7 dumps (catalog, not offered).
  The offered NAMES sit 2–3 pointer hops deeper (behind `textBuf`) than the depth-1 dump ever follows.
- **0.5.4 read recipe CONFIRMED** (use when building Task 2): the rumour text-struct guard at struct
  `+0x10` CHANGED on 0.5.4 to **`91 9C 9F FF 00 01 00 00`** (byte[4] flipped `01→00`). `body+0xC8` = a flat
  inline buffer (font-name run then flavour text); `body+0x138` = the text-struct (guard at +0x10, packed
  inline UTF-16 runs at +0x18). Skip runs that are font names ("Fontin Smallcaps", "miBold", "Bold").
- **Offered = the panel's slot widgets** — a small set (the 2–3 visible rumour rows, depth-4 children of
  the rumour-panel container), DISTINCT from the huge ~1088-entry catalog scroll list. Reading "offered" =
  read those few slot widgets' names via the deep deref — NOT a flag on catalog entries.
- **dump7 (Saga) couldn't be resolved** to either state — the text was overwritten at the dump's 0x80 boundary.

## DONE — v4 deep-capture build shipped (v0.5.1-rumourdiag4); awaiting ONE capture

**v3 (rumourdiag3) FAILED** (1 capture, analysed): it hit the 6000-element DFS cap AND only followed
"guard-structs" which turned out to be MOD text ("Open all Strongboxes" — Bleak's mod, captured even
though Bleak wasn't offered = catalog), not names. The offered names (Cold/Endless/Nothin) were **0 hits**
in its dump. **v4 (`v0.5.1-rumourdiag4`, branch `diag/rumour-0.5.4`, commit `6594498`; Pre-release, v0.5.1
stays Latest)** fixes both, built via the FULL superpowers flow (brainstorm → spec
`specs/2026-06-25-rumour-discovery-dump-v4-design.md` → plan `plans/2026-06-25-rumour-discovery-dump-v4.md`
→ subagent-driven-development: implementer + task review **SPEC ✅ + Quality Approved**). v4 `UiDump.cs`:
- **BFS walk** (Queue, was DFS Stack) — covers the shallow panel (~depth 4–6) before deep subtrees.
- **Wide bounded 3-hop text net** (`WideTextNet`, NO guard gating): for each element, follow ALL body
  pointers 3 hops, emit EVERY UTF-16 run (font runs tagged `(font)`), deduped per element, capped at 256
  reads/element. Emits a `TEXT elem=…` block (with `addr/depth/parent/children/vis`) for any element with
  a non-font string. The offered names surface whatever structure holds them — no offset guessing.

Approach = **wide-net DISCOVERY** (the user confirmed the panel IS the source of truth): one capture to
learn the panel fingerprint + name chain → THEN a clean panel-anchored shipped reader (the wide net never
ships). Message to the user's user drafted (download rumourdiag4 → ONE capture + screenshot).

## When the deep dump arrives — analysis plan

1. Decompress; read the top **`=== DEEP TEXT CAPTURE ===`** section. Grep for the offered names from the
   screenshot (e.g. `cliff`, `Cold`, `drink`) — they appear as non-`(font)` runs under a `TEXT elem=` block.
2. List the matching slot elements + their context (`elem=/depth=/parent=/children=/vis=`) and the winning
   `hN+0xNNN` hop offset where the name was found.
3. Cross-ref with the screenshot's offered set → the slot elements' shared `parent=` = the **offered-slot
   anchor** (the panel container; its few slot widgets = offered, distinct from the catalog list).
4. Pin the 0.5.4 name read path from the winning hop offsets → update `Poe2.IslandRumour` for Task 2.
5. Update the spec (replace the wrong visible-chain filter with: **offered = the panel-slot widgets under the
   anchor parent, read via the deep deref**) + plan Task 2.
6. Resume subagent-driven-development: fix Task 2 (re-review) → Task 3 (wiring/config) → Task 4 (display
   panel + dashboard) → Task 5 (integration sweep + final whole-branch review) → release.

## Existing dump artifacts (first batch, 0.5.x)

- GOLD (the rumour screen): `/tmp/rumourdump/1782356965.txt` (decompressed; may be gone in a new session).
- Negatives (hideout/stash, zero rumour strings — confirmed no off-screen false positives):
  `_1782356773`, `_1782356814`, `_1782356835`.
- Persistent originals: `C:\Users\minec\Downloads\ui-dump-*.txt.gz`.
- Tier table source: `C:\Users\minec\Downloads\Dracorath - Expedition Explained - Expidition Cheat Sheet.csv`
  (25 entries; columns Type/Rumor/Map/Mods/Tier/Note; NO prices). Already embedded in `island_rumours.json`.

## Cleanup (once the feature lands)

- Drop `stash@{0}` ("rogue agent island-rumours build" — UNUSED).
- Delete ALL throwaway diag prereleases + tags (`v0.5.1-rumourdiag`, `-rumourdiag2`, `-rumourdiag3`,
  `-rumourdiag4`) + BOTH branches (`feat/rumour-diag`, `diag/rumour-0.5.4`) + the `UiDump` diagnostic
  (UiDump.cs, the `/api/diag/ui-dump` endpoint, the Diagnostics dashboard card, `RadarApp.UiDumpDiag`).
  Removal checklist in `.superpowers/sdd/rumour-diag-report.md`.
