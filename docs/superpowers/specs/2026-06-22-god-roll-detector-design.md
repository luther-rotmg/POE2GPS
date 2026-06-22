# God-Roll Detector — Design Spec

- **Date:** 2026-06-22
- **Status:** Approved 2026-06-22 (Ryan: "yes but default off"). **Ships default OFF / experimental.**
- **Topic:** A read-only 0–100 gear scorer driven by user stat weights — "did I just get a god roll?" at a
  glance — surfaced in a dashboard **Gear** tab (and, later, an opt-in overlay toast).

---

## 1. Summary & the user need

You pick up a rare and want to *instantly* know if it's special, without parsing six affixes. The
detector scores each item **0–100** against a **stat-weight table you control** (`config/
stat_weights.json`) and flags anything over your god-roll threshold with a ⭐. It reads your inventory +
each item's rolled affixes (offsets already validated in `Poe2Offsets.cs`), computes the score
**locally**, and shows it in the dashboard. No trade/economy data — your rolls × your weights, fully
offline.

## 2. Invariants

- **I1 — Read-only.** Inventory reads + local JSON weights only. No input/process-write. Gate stays green.
- **I2 — Default OFF / experimental.** `RadarSettings.EnableGearScorer = false`. Nothing reads inventory
  or renders unless the user opts in. Safe to ship even before a live smoke-test.
- **I3 — No identifying data** in any payload (item stat lines + scores only; no character name).
- **I4 — Reuse.** Scoring stat-lines come from the existing `ItemModTranslator.RenderMod`; inventory
  offsets are the validated `Poe2Offsets.ServerData.*` set; the read logic is a faithful port of the
  Research `RunInventory` probe (`POE2Radar.Research/Program.cs:527`), which self-validates the drift-prone
  vec hops. The dashboard tab reuses the Director/Atlas tab pattern.
- **I5 — Tool scores; human weights.** Weights + threshold are the user's; the tool never decides what's
  "good" — it applies the user's weights.

## 3. Architecture & data flow

```
world tick (gated on EnableGearScorer, SLOW cadence ~1-2 Hz — inventory changes slowly)
   └─ Poe2Live.ReadInventory()  (PORT of Research RunInventory; validated offsets)
        → for each item: rarity, identified, name, and affixes → ItemModTranslator.RenderMod(modId, values)
          → flat list of (statLine, statIds, value) per item
   └─ GearScorer.Score(item.affixes, StatWeights)   (PURE, Core, unit-tested)
        → { score 0-100, perAffix contributions, isGodRoll = score >= threshold }
   └─ publish GearSnapshot (immutable, lock-free swap like the others)
        ▼
   GET /api/gear → [{ name, rarity, slot, score, godRoll, affixes:[{line, value, weight, points}] }]  (no identifying data)
        ▼
   Dashboard "Gear" tab (hidden unless EnableGearScorer): item list sorted by score, ⭐ on god rolls,
   click → per-affix breakdown; a Weights editor (stat → weight) + threshold + target.
```

## 4. Scoring (the pure heart — `Core/Gear/GearScorer.cs`)

- **Input:** `IReadOnlyList<Affix>` where `Affix { string StatLine, IReadOnlyList<string> StatIds, double Value }`,
  plus a `StatWeights` (`{ Dictionary<string,double> ByStatId, double Target, double GodRollThreshold }`).
- **Score:** `raw = Σ over affixes of (Value × weightFor(affix))` where `weightFor` looks up the max weight
  among the affix's `StatIds` (0 if none weighted). `score = clamp(raw / Target × 100, 0, 100)`.
- **Output:** `GearScore { double Score, bool IsGodRoll (Score >= GodRollThreshold×100? no — threshold is a
  0-100 number; IsGodRoll = Score >= GodRollThreshold), List<AffixContribution> Affixes }` where
  `AffixContribution { string Line, double Value, double Weight, double Points }`.
- **Pure + deterministic → fully unit-tested:** weighted sum, clamping at 0 and 100, unweighted affixes
  contribute 0, multi-stat affix takes the max weight, empty item → 0, threshold boundary.

A **starter `stat_weights.json`** ships (Life/ES/Resists/movement/common damage at sensible weights) so the
feature is useful out of the box; the user tunes it in the dashboard.

## 5. Components / files

**New:**
- `Core/Gear/GearScorer.cs` — `Affix`, `StatWeights`, `GearScore`/`AffixContribution` records + `GearScorer.Score(...)` (pure). **Unit-tested.**
- `Overlay/Web/GearWeightStore.cs` — owns `config/stat_weights.json` (load/save via `JsonStore`, starter defaults if absent), exposes `StatWeights` snapshot + an update method.
- Dashboard **Gear tab** in `DashboardHtml.cs` (`data-tab="gear"` — distinct namespace), hidden unless `EnableGearScorer`.

**Extended:**
- `Core/Game/Poe2Live.cs` — add `ReadInventory()` (port of Research `RunInventory`): walk
  `AreaInstance+0x580 → ServerData → +0x48[0] → +0x320 PlayerInventories` → items → affix vecs (Implicit
  `+0xA0`/Explicit `+0xB8`) → mod ids + rolled values → `ItemModTranslator.RenderMod`. Returns a list of
  `InventoryItem { Name, Rarity, Identified, Slot, Affixes }`. **Needs a live smoke-test (validated
  offsets, faithful port — but unverifiable without the game; default-off contains any risk).**
- `Overlay/RadarApp.cs` — when `EnableGearScorer`, on a slow cadence read inventory (on the world reader),
  score each item, publish a `GearSnapshot`; pass a provider to `ApiServer`.
- `Overlay/Web/ApiServer.cs` — `GET /api/gear` (scored items, no identifying data) + `GET/POST
  /api/gear-weights` (loopback-gated edit of the weight table).
- `Config/RadarSettings.cs` — `EnableGearScorer = false` + the settings round-trip + dashboard toggle.

## 6. The UI-clunkiness answer

- **Primary surface is the dashboard Gear tab** — zero overlay clutter, it's a "review my loot" screen.
- **Off by default**; one toggle in Settings turns it on.
- **Overlay toast (deferred to v2):** an opt-in self-dismissing "⭐ god roll — 94/100" when an item above
  threshold first appears — the "you'll know it the moment it drops" payoff without a persistent HUD.

## 7. Testing

- `GearScorer.Score` — pure xUnit (weighted sum, clamp 0/100, unweighted=0, max-weight on multi-stat,
  threshold boundary, empty item). The testable heart.
- `GearWeightStore` defaults/round-trip — light.
- `ReadInventory` + the tab + endpoints — **manual release-checklist (needs the live game)**; this is the
  part the default-off flag protects until smoke-tested.

## 8. Out of scope (v1)

- The overlay toast (v2). Per-build weight *presets* (one set for now; user edits it). Tier-range
  "roll quality" mode (needs Mods.dat ranges we don't embed). Equipped-vs-bag distinction beyond the slot
  label.

## 9. Risks

- **Inventory read is a drift-prone port** — mitigated by reusing the validated offsets + the Research
  probe's self-validating fallbacks, and by **default-off** so an unvalidated read harms nothing until the
  user smoke-tests it. Flag clearly in the release notes as experimental.
- **World-thread cost** — gated on the flag + a slow cadence (inventory changes slowly), so when off it's
  zero and when on it's a once-a-second read, not per-tick.
