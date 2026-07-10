# POE2GPS "Threshold" Design Spec

**Date:** 2026-07-09
**Prior release context:** v0.21 "Guided Campaign" still pending tag (blocked on PMS-12 + PMS-13). Campaign Probe merged to main today (`1ab7bba`). Threshold ships parallel to the v0.21 tag routine — new feature drop, no v0.21 surface changes.

---

## 1. Goal

Two-track hygiene + feature drop:

1. **Upstream sync (3 surgical items).** POE2GPS already synced the big upstream `Sikaka/POE2Radar v0.16.0` atlas overhaul in v0.13.0 (7 features, byte-identical port). Only three tail commits since fork are genuinely useful and compliance-clean:
   - Atlas content-icon `DrawBitmap` destination-rect fix (Sikaka v0.16.6).
   - `WaygateDevice` built-in Tile display rule + one-shot migration (Sikaka v0.16.6).
   - Click-to-collapse nearby-monolith reward panel (Sikaka v0.15.3).
2. **XP/hour Session HUD chip (PMS-6 free-rider).** Campaign Probe shipped `Poe2Live.PlayerExperience(nint) → long` reading `PlayerComponent + 0x1D8`. That accessor closes Long List #34 (XP/hour Session HUD chip). New row in the existing `SessionHud` panel — no new panel, opt-in default OFF, reuses the shipped embedded `xp_curve.json`.

**Release theme:** "Threshold" — literal (Waygate/monolith-adjacent) + figurative (XP threshold to next level). Matches POE2GPS's short-word-forward theming convention (Roadclearing / Guided Campaign).

## 2. Non-negotiables

- **Zero memory writes.** No `MemoryReader.Write*`, no `Marshal.Write*`, no new offset writes. Pre-merge grep gate covers input-injection patterns (`SendInput`, `keybd_event`, `mouse_event`, `WriteProcessMemory`, `VirtualProtect`, `PostMessage(WM_KEY|WM_MOUSE|WM_LBUTTON)`) explicitly.
- **Zero-cost-when-off (XP HUD).** With `ShowXpRate=false`, the fallback `_live.PlayerExperience(localPlayer)` read at ~5 s cadence is NOT called at all — gated in `RadarApp.WorldTick`. Spy test asserts.
- **Ring survives zone crossings.** XP/hour is a grind metric, not a zone metric. Town frames don't append (reuses `ExcludeTownsFromPace` — no new knob). `SessionTracker.Reset(now)` clears + re-seeds so first post-reset tick reads delta=0 cleanly.
- **XP curve DEDUP.** Reuse the existing embedded `xp_curve.json` at `src/POE2Radar.Core/Campaign/Guide/Data/poe2/xp_curve.json` (100-entry cumulative, sourced poe2db.tw). **DO NOT** create `Poe2XpCurve.cs` with a duplicate `static long[]` — two sources of truth silently drift on any future PoE2 XP rebalance.
- **SessionTracker.Update signature stability.** Ship as a 9-arg OVERLOAD delegating from the existing 8-arg signature (`currentXp=0` handled as "skip append, re-emit prior rate"). A hard signature change would ripple through every existing SessionHud caller.
- **`AppliedMigrations` adapter.** Add defensive `SettingsMigrator.Map` legacy-bool → key entry for `BuiltInTileRulesSeeded` (even though upstream fork has no such bool). The v0.20.1 `AppliedMigrations` refactor pattern requires this defensive shape.
- **Monolith collapse preserves `ctx.MonolithsTop` pre-sort/cap-to-6.** Do NOT mechanically copy upstream's inline `OrderByDescending+Take+ToList` — that regresses v0.13.x monolith prioritization.
- **`DrawBitmap` fix ports by PATTERN match**, not line-number match. Upstream `L416` is a snapshot; only the pattern `rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH))` is stable.
- **Waygate double-marker risk.** Item #2 (built-in Tile display rule) seeds a `DisplayRule` matching game-object `Name='WaygateDevice'`. When a future R5 Waygate atlas-landmark port lands, it will match the same entity via `Classify()` and could stamp two markers. Bake into item #2's tests: `Waygate entity produces exactly ONE marker with the new rule`. Future atlas port fixes the source, not this rule.
- **No upstream repo names leak into public surfaces.** "Sikaka", "GameHelper", "upstream repo names" must NOT appear in GitHub release body, Discord post, README, or in-app strings. Per `feedback_no_internal_tooling_in_public_surfaces` memory. Bake into pre-tag grep gate.

## 3. Locked LO decisions (2026-07-09)

- **Q1: R5 atlas_maps.json / atlas_content.json / AtlasIcons refresh** — DEFER to a followup "AtlasRefresh" drop after the actual v0.16.0+ upstream JSON diff pass. Bloats Threshold from 13 → 19 tasks with unmeasured surface; if any tags renamed upstream we need a `SettingsMigrator` to rewrite user `AtlasGroup.Tags` or saved color-group filters silently break on upgrade.
- **Q2: PriceBook MinQuantity fix** — SKIP PERMANENTLY. Confirmed the ground-loot pricing surface is fully stripped (zero `MinQuantity` hits in POE2GPS src/, `ScanLootLabels` has zero callers). Add a sync-audit note so future upstream MinQuantity changes auto-skip.
- **Q3: /api/session-reset HTTP route** — DEFER to a dedicated dashboard UX drop. Expands API surface mid-flight and needs its own auth/idempotency thought.

## 4. Architecture — the 4 items in detail

### 4.1 Atlas content-icon `DrawBitmap` fix (1 task)

**Where:** `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` `DrawAtlasContentIcons` method.

**What:** upstream Sikaka v0.16.6 (`a9897d3`) discovered that the destination-rect coordinates on the atlas content icons were wrong — icons drew at the wrong screen position when the atlas was zoomed. One-line correction: swap the `iconH`/`iconW` refs so the rect is square with the correct origin.

**Port method:** pattern-match on:
```csharp
rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(ix, iy, ix + iconH, iy + iconH));
```
The upstream fix is a semantic pattern, not a line-numbered patch — port by grep + edit rather than line-number diff.

**Test:** `OverlayRenderer.DrawAtlasContentIcons_DestinationRectIsSquareAndOriginAligned` — synthetic icon at a known coord, assert rect corners.

### 4.2 `WaygateDevice` built-in Tile display rule (2 tasks)

**Where:** `src/POE2Radar.Overlay/Web/DisplayRules.cs` (new `BuiltInTileRules()` static) + `src/POE2Radar.Overlay/Config/SettingsMigrator.cs` (new migration entry).

**What:** upstream seeds a `Navigable=true, Shape=Eye, Color=#<hex>` rule for entities named `WaygateDevice` so end-game waygates render as a distinct tracked marker. One-shot migration: `applied_migrations` gets a new entry `built_in_tile_rules_v1` — idempotent, additive-only (won't re-seed on subsequent boots).

**Defensive migrator:** even though upstream fork has no `BuiltInTileRulesSeeded` legacy bool, add the `SettingsMigrator.Map` entry pointing that legacy bool at the new key. The v0.20.1 refactor pattern requires this for consistency; future refactors that check "was this migration ever run" work uniformly.

**Waygate double-marker risk:** future R5 atlas-landmark port will match the same entity via `Classify()` and could stamp two markers. Test asserts `WaygateDevice entity produces exactly ONE marker with the new rule` — if the future R5 port lands, IT fixes at source (removes the built-in rule or short-circuits `Classify()`), not this rule.

**Tests:**
- `SettingsMigrator_BuiltInTileRulesV1_SeedsWaygateRuleOnce` (idempotent).
- `DisplayRules_WaygateDeviceMatches_ExactlyOneMarker`.

### 4.3 Click-to-collapse nearby-monolith reward panel (1 task)

**Where:** `MonolithSettings.cs` (add `PanelCollapsed: bool` persisted) + `OverlayRenderer.DrawMonolithPanel` (caret in title + `mono-collapse` hit-rect via `_legendClickables` list).

**What:** upstream `d8aaa6a` (v0.15.3) added a click-to-collapse caret on the nearby-monolith reward panel's title bar. Click toggles a persisted bool; collapsed state hides everything except the title row.

**Preserve `ctx.MonolithsTop` semantics:** upstream refactored this section with an inline `OrderByDescending+Take+ToList`. POE2GPS's shipped path pre-sorts + caps at 6 elsewhere. Port ONLY the collapse toggle + caret + hit-rect; leave the sort/cap logic alone.

**Test:** `OverlayRenderer_DrawMonolithPanel_CollapsedStateHidesRewardRows` (mocked collapse=true, assert row count).

### 4.4 XP/hour Session HUD chip (9 tasks)

**Where:**
- `src/POE2Radar.Core/Session/SessionTracker.cs` — new ring buffer + 9-arg `Update` overload.
- `src/POE2Radar.Core/Session/PoE2XpCurveLoader.cs` (or similar location) — static loader for existing `xp_curve.json`. **DO NOT** create a `Poe2XpCurve.cs` with a duplicate `static long[]`.
- `src/POE2Radar.Overlay/Overlay/OverlayRenderer.cs` `DrawSessionHud` (~L875-991) — new row appended after existing `ShowKills` block.
- `src/POE2Radar.Overlay/Config/SessionHudSettings.cs` — new `ShowXpRate: bool = false` + `XpWindowMinutes: int = 5` (clamped 1..60).
- `src/POE2Radar.Overlay/Web/DashboardHtml.cs` + `Web/ApiServer.cs` — mirror settings + row toggle in dashboard following existing per-row-toggle pattern.
- `src/POE2Radar.Overlay/RadarApp.cs` `WorldTick` — gated `_live.PlayerExperience(localPlayer)` fallback read at ~5 s cadence (piggybacks on existing `_levelRefreshTick`).

**Row format:**
```
XP/hr    1.24M  (12m to L86)
```
Single line when both fit; splits to two lines while ring is still filling. Number humanization: `<1K` raw, `<10K` `1.24K`, `<1M` `245K`, `<1B` `1.24M`, else `2.1B`. Yellow tint when `XpPerHour == 0` (window empty or player unresolved) — matches existing "no data" tell used by deaths row.

**Computation:**
- Fixed-slot ring buffer of `(nowTicks, cumulativeXp)` samples inside `SessionTracker`, sized `slots = max(12, XpWindowMinutes * 12)` at ~5 s cadence. Round-robin index. Zero per-tick allocation.
- Rate = `(latest - oldest_in_window) / windowHours`.
- Fallback: `XpPerHourSession = SessionXpDelta / sessionHours` while ring is still filling.
- Cumulative uint32 (poe2db curve caps ~4.25B — fits all 100 levels per `Poe2Live.cs:1983-85` comment). Widened to `long` via `(long)value` cast per Campaign Probe's shipped pattern.
- When `currentXp <= 0` (player component unresolved): skip append, re-emit previous rate. No NaN, no zeroing spike.

**Zone-crossing behavior:**
- Ring survives zone transitions (grind metric, not zone metric).
- Town frames DON'T append — reuses existing `ExcludeTownsFromPace` toggle. Rate freezes on town entry then slides down as older samples age out of window.
- `SessionTracker.Reset(nowTicks)` (Ctrl+Alt+R already shipped) clears ring + re-seeds with current XP sample so first post-reset tick reads delta=0.

**Time-to-level:**
- Reuse existing `xp_curve.json` at `Campaign/Guide/Data/poe2/xp_curve.json` (100-entry cumulative, index 0 = L1, sourced from poe2db.tw).
- New static loader `PoE2XpCurveLoader` in `POE2Radar.Core/Session/`: reads embedded resource once at init, exposes `XpToNextLevel(int level, long currentXp) → long?` + `TimeToNextLevel(int level, long currentXp, float xpPerHour) → TimeSpan?`.
- `TimeToNextLevel` returns `null` when `xpPerHour <= 0` OR `level >= 100`. Renderer suppresses `(Nm to L##)` segment on null.

**User controls:**
- `SessionHudSettings.ShowXpRate: bool = false` — opt-in default OFF per PMS-6 policy and Session HUD stance.
- `SessionHudSettings.XpWindowMinutes: int = 5` — clamped 1..60 in setter (5-min vs 30-min is a real preference split among grinders).
- Mirrored in `/api/settings` GET/POST as `sessionHudShowXpRate` + `sessionHudXpWindowMinutes` (existing per-row-toggle pattern: `ShowPace`/`ShowZoneContext`/`ShowDeaths`/`ShowKills`).
- Reset piggybacks on existing Ctrl+Alt+R session reset — no new hotkey.
- Town-freeze reuses `ExcludeTownsFromPace` — no new knob.

**Zero-cost-when-off gate:** in `RadarApp.WorldTick`, if `!SessionHud.Enabled || !SessionHud.ShowXpRate`, the `_live.PlayerExperience(localPlayer)` call is NOT invoked. Spy test asserts zero allocations across 1000 disabled ticks.

**`SessionTracker.Update` overload:**
```csharp
// Existing 8-arg (untouched — all shipped callers keep working):
public void Update(...);

// New 9-arg overload, delegates internally:
public void Update(..., long currentXp);
```
`currentXp=0` handled as "skip append, re-emit prior rate" so callers with no XP source (test harness, early boot) don't crash.

## 5. Task list preview

**9 tasks, single track:**

1. `THR-DRAW-FIX` — Atlas content-icon `DrawBitmap` fix (pattern-matched port).
2. `THR-WAYGATE-RULE` — `WaygateDevice` display rule + defensive `SettingsMigrator` map entry.
3. `THR-WAYGATE-TESTS` — Idempotent migration + exactly-one-marker assertions.
4. `THR-MONO-COLLAPSE` — Click-to-collapse monolith reward panel (caret + hit-rect + persisted bool). Preserves `ctx.MonolithsTop` semantics.
5. `THR-XP-CURVE-LOADER` — `PoE2XpCurveLoader` static reading existing embedded `xp_curve.json`. NO duplicate array.
6. `THR-XP-TRACKER` — `SessionTracker` ring buffer + 9-arg `Update` overload + zone/town semantics + `Reset(now)`.
7. `THR-XP-SETTINGS` — `SessionHudSettings.ShowXpRate` + `XpWindowMinutes` + `/api/settings` mirroring.
8. `THR-XP-RENDER` — `DrawSessionHud` new row + humanization + yellow-tint no-data + zero-cost-when-off gate in `WorldTick`.
9. `THR-XP-TESTS + DOCS` — Full test set (spy, zone/town semantics, curve loader, humanization, Reset re-seed) + PMS-6 tracker moves to Done + CHANGELOG themed body.

## 6. Ordering

- Tasks 1-4 (atlas + monolith) are independent — parallelizable if bandwidth allows.
- Tasks 5-7 (XP curve → tracker → settings) serial: loader → tracker → settings mirror.
- Task 8 after 5-7.
- Task 9 after all.

## 7. Non-goals for Threshold

- No R5 atlas_maps.json / atlas_content.json / AtlasIcons refresh (deferred).
- No PriceBook MinQuantity fix (ground-loot pricing surface stripped — confirmed).
- No `/api/session-reset` HTTP route (deferred).
- No R5 Waygate atlas-landmark family port (defer to atlas-refresh drop).
- No Currency Exchange (compliance).
- No HoverPrice tooltip (out of scope for navigation fork).
- No new hotkeys (Reset reuses Ctrl+Alt+R).
- No new panels (XP row lives inside existing `SessionHud`).
- No duplicate XP curve (dedup non-negotiable).
- No hard `Update` signature change (overload instead).
- No new memory-read cadence (piggyback on existing `_levelRefreshTick`).

## 8. Risks

1. **Waygate double-marker.** Item #2 seeds a rule; future R5 atlas-port could stamp a second marker for the same entity. Test asserts single marker; future port fixes at source.
2. **`DrawBitmap` fix line drift.** Ship as pattern-match port, not line-numbered patch.
3. **`SessionTracker.Update` breaking existing callers.** Ship as OVERLOAD, not signature change.
4. **XP curve drift with Campaign Guide.** Deferred by reusing existing `xp_curve.json`. No second source.
5. **Zone-crossing XP jitter.** Ring survives crossings. Town frames don't append. Reset re-seeds cleanly.
6. **Zero-cost-when-off regression.** Spy test locks. WorldTick gates the `PlayerExperience` call.
7. **Public-surface leak.** Grep gate on release body, Discord post, README, in-app strings for "Sikaka", "GameHelper", upstream repo names.
8. **`AppliedMigrations` idempotency.** Defensive `SettingsMigrator.Map` entry protects the future-refactor path.
9. **Monolith prioritization regression.** Preserve `ctx.MonolithsTop` pre-sort/cap-to-6 in the collapse port.

## 9. Deploy sequence

1. Feature branch `feat/threshold`, base current main HEAD.
2. Task-by-task SDD execution (9 tasks).
3. Whole-branch review.
4. Merge to main via `--no-ff` release boundary commit.
5. Tag `v0.22.0` (if v0.21 hasn't tagged yet, this might be v0.22.0 or a v0.22.0-preview — depends on PMS-12/13 timing).
6. Standard release routine: CHANGELOG themed rewrite → gates → tag → CI attaches zip + SHA-256 → `gh release edit --title` → Discord post.

## 10. Pending Manual Steps (updated)

- **PMS-6** (Long List #34 XP/hour Session HUD chip) — **CLOSED by Threshold** via `Poe2Live.PlayerExperience` accessor + new `SessionHud` row. Move to Done in `docs/pending-manual-steps.md` when Task 9 lands.
- No new PMS entries.

---

**Ready for LO review.** On `go`: invoke writing-plans workflow → SDD execution.
