# Island Rumours — Resume Packet (2026-06-25)

Single source of truth to resume the **Expedition "Island Rumours"** feature. Read this first.

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

## What's needed next — ONE definitive DEEP capture (not more shallow dumps)

The shallow `UiDump` (depth-1, 0x80) can't reach the offered names. Need a targeted **deep-deref** capture:
for each rumour slot widget, follow `body+0x138 → struct` (verify guard `91 9C 9F FF 00 01 00 00`) `→ struct+0x18`
(inline string OR `textBuf` pointer `→ +0x08`) → UTF-16 name (skip Fontin runs), capturing ~0x200/0x100 bytes
per hop. Anchor on the rumour-panel slot widgets. One small, complete capture confirms the offered names + the
slot structure → then build Task 2 with certainty. Full deep-probe spec in the mine output (workflow `wofol5efd`).

## When the dumps arrive — analysis plan

1. Decompress each dump; grep for the offered strings per its ground-truth note.
2. **Re-validate the recipe on 0.5.4:** confirm `+0x138` text-struct, magic-guard bytes, str offsets
   still hold (or re-derive). Update `Poe2.IslandRumour` if shifted.
3. **Diff the offered sets** across screens vs the constant catalog → find the structural discriminator
   that isolates offered (a flag, a distinct parent container, the vector the 3 offered live in — the
   depth-3 `+0x190` 3-string lead).
4. **Decode the area-ID signal** (Caesar−3) at depth-3 `+0x190` → area names → cross-ref the offered labels.
5. Update the spec (replace the wrong visible-chain filter with the real discriminator) + plan Task 2.
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
- Delete BOTH throwaway diag prereleases + tags (`v0.5.1-rumourdiag`, `v0.5.1-rumourdiag2`) + BOTH
  branches (`feat/rumour-diag`, `diag/rumour-0.5.4`) + the `UiDump` diagnostic (UiDump.cs, the
  `/api/diag/ui-dump` endpoint, the Diagnostics dashboard card, `RadarApp.UiDumpDiag`). Removal
  checklist in `.superpowers/sdd/rumour-diag-report.md`.
