# Experimental Features — Design Menu (awaiting approval)

- **Date:** 2026-06-22 (drafted overnight)
- **Status:** DRAFT — **awaiting Ryan's morning approval.** Nothing here is built or shipped yet.
- **Topic:** A ranked menu of read-only, user-QoL feature ideas — led by the **God-Roll Detector** Ryan
  asked for — to pick from. Each stays inside the project invariants (read-only, no input emission, no
  process writes, no identifying data, compliance gate green).

> Ryan's ask: *"draft an experimental feature that ranks gear 0/100 based on stat weights … god roll
> detector … make it convenient to turn on/off and not clunky UI-wise … brainstorm in this vein thinking
> of the users' deep wants/needs … QoL."* This doc answers that and offers neighbours to choose among.

---

## 1. ⭐ God-Roll Detector (the headline)

**The user need.** You pick up a rare and want to *instantly* know "is this special?" without parsing six
affixes in your head or alt-tabbing to a trade site. A 0–100 score that says "this is a god roll" turns a
moment of squinting into a glance.

**What we can already read (feasibility ✓).** The overlay already reads inventory items and their rolled
mods — `ItemModTranslator` renders internal mod ids + the *rolled values read from memory* into the
game's stat lines (validated via Research `--inventory --itemmods`). So we have each item's affixes and
their numeric rolls in hand. No new memory research needed for a first version.

**Scoring — two modes, ship mode B first:**
- **(A) Build-agnostic "roll quality"** — for each affix, what % of its tier's value range did it roll?
  Average them → "numerically high rolls." *Problem:* needs per-mod min/max tier ranges, which we don't
  have embedded yet (would need the Mods.dat ranges). Defer.
- **(B) Build-weighted score (recommended v1)** — a user-defined **stat-weight table** (`config/
  stat_weights.json`, editable in the dashboard): weight per stat line (Life 1.0, %PhysDamage 0.8, Resist
  0.3, …). Score = `clamp(Σ(rolledValue × weight) / target × 100, 0, 100)` where `target` is a tunable
  "this-is-a-100" total. Needs only the rolled values we already read + weights the user sets. Build-aware
  by definition ("good *for my build*"). A starter weight set ships; the user tweaks.

**The UI problem Ryan flagged ("not clunky").** Three surfaces, least-intrusive first:
1. **Dashboard "Gear" tab** *(primary, zero overlay clutter)* — lists your inventory items with their
   0–100 score + a ⭐ on anything over your god-roll threshold; click to see the per-affix contribution.
   This is the "review my loot" surface and never touches the play screen.
2. **Overlay toast (opt-in)** — when an item *enters your inventory* scoring above the threshold, a small
   self-dismissing corner toast: "⭐ God roll — 94/100." This is the "you'll know it the moment it drops"
   payoff, with no persistent HUD. One toggle + a threshold slider.
3. **Hotkey peek (later)** — a key to score the item currently under the cursor. Needs cursor-item reads
   (more work); defer.

**Invariants.** Read-only (inventory reads + local JSON weights). Off by default (experimental). No
economy/trade data — it's *your* rolls × *your* weights, fully offline. No identifying data in payloads.

**Rough effort.** Medium: a `GearScorer` (Core, pure + unit-testable: weights × stat lines → score), an
inventory→stat-lines read path (largely exists), `config/stat_weights.json` + a dashboard Gear tab, and
the optional toast. The pure scorer is the testable heart; everything else is wiring we've done before.

---

## 2. Neighbours in the same vein (pick any; ranked by QoL/effort)

1. **🔊 Turn-by-turn audio cues** *(highest delight, low effort)* — spoken or tonal callouts: "objective
   reached," "unexplored exit behind you," "boss ahead," and "⭐ god roll" (pairs with #1). Output-only
   (no input), uniquely fitting for something literally called a GPS. The "voice in the car" the overlay
   is missing.
2. **🧭 Compass / bearing strip** — a thin top-of-screen band with direction + distance to each tracked
   objective, so you stop watching the minimap. Complements the existing route line.
3. **💰 Session currency tracker** — read inventory currency *stack counts* and show session deltas
   ("+12 Exalts, +3 Divines this run"). Pure counts, no economy values — farming QoL.
4. **📊 Zone HUD** — explored %, time-in-zone, objectives-remaining. Speed-farming feedback; reuses the
   fog-of-war + Director data already computed.
5. **📌 Pinned waypoint / "retrace"** — one key to route back to the zone entrance or a pinned spot.
6. **🗺️ Loot snapshot** — log item *names* that dropped in a zone for after-run review (no values).

All are read-only / output-only and fit the existing radar + dashboard + (for audio) a tiny output layer.

---

## 3. Recommendation

Ship the **God-Roll Detector (mode B)** with the **dashboard Gear tab + opt-in toast**, and pair it with
**audio cues (#2.1)** so the "you got something great" moment lands without you watching a number. Those
two together are the strongest QoL-per-effort and most on-theme. The rest are a backlog to pull from.

**Decision needed:** which of these to build first (my pick: God-Roll Detector + audio cues), and for the
detector, confirm **mode B (build-weighted)** for v1. On approval I'll brainstorm → spec → plan → build
each properly.
