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

## What we're waiting for

**2–3 more "Dump Rumours" captures from DIFFERENT rumour screens** (different offered sets), EACH with
a note/screenshot of **WHICH rumours were showing** (ground truth). The offered set changes between
screens while the catalog is constant → diffing reveals the true offered-marker + lets us decode the
area-ID signal. (Message drafted; see the chat hand-off / below.)

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
