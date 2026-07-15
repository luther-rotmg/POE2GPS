# Gold Tier: 4-Release Arc (v0.35 → v0.38)

**Status:** Approved by LO 2026-07-15 (brainstorm phase). Awaiting spec review before writing-plans.
**Supersedes:** 2026-07-14 Gold-tier brainstorm (Custom Entity Icons v0.36 + Character Codex v0.37) — folded into this arc.

## Summary

Four consecutive releases build one coherent Gold-tier narrative: **PALETTE → ICONS → CHRONICLE → FORGE**. Every feature was triaged against a verified signal audit of the POE2GPS memory-read pipeline (no log tailing exists; drops/entities/vitals/area/position/kills/deaths/boss-catalog all live). Nine candidates were cut to keep the arc coherent — six for narrative fit (defer to v0.39+ Streamer Kit / Advanced Analytics / Dashboard Studio), one for scope (Layout Designer XL), one for missing subsystems (Session Wrapped — no currency reader, no PriceBook, no market API).

## Arc narrative

| Release | Beat | Flagship | Reader thesis |
|---|---|---|---|
| v0.35 | PALETTE | Curated Theme Pack (8 palettes) | "Gold arrives — visible on dashboard AND on stream" |
| v0.36 | ICONS | Custom Entity Icons | "The overlay itself is yours" |
| v0.37 | CHRONICLE | Character Codex | "Your character has a story now" |
| v0.38 | FORGE | Color Forge | "Author your own themes" |

Total scope: ~3100-3900 LOC across four releases, ~700-1200 LOC per drop. Matches current pipeline pace with hotfix headroom.

## Constraints (any violation → cut)

- **Read-only preserved.** No SendInput, no game injection, no new memory-write reach.
- **No telemetry.** No phone-home beyond the already-approved update check + supporter-code Cloudflare Worker.
- **Local-first.** Persist to `config/`. No cloud unless via the existing Worker.
- **Additive.** No existing free/flat feature is gated behind a Gold check.
- **No new game-memory reach.** All signals must already be read by the tool today.

## Signal audit reference

Verified 2026-07-15 by four-agent parallel read of `src/POE2Radar.Core/{Game,Session,Gear,Presence,MemoryReader.cs}` and `src/POE2Radar.Overlay/Web/`. Full audit persisted at `scratchpad/gold-tier-signal-audit-w25celc59.json`.

**Available today:**
- Position: `Poe2Live.PlayerGrid/PlayerWorld` per tick, camera matrix for world→screen
- Vitals: `Poe2Live.PlayerVitals` (HP/Mana/ES cur+unreserved + derived %), monster HP via `EntityDot.HpCur/HpMax`, self-healing offset auto-relocate
- Progression: `Poe2Live.PlayerLevel`, `PlayerExperience` (raw uint32, no XP-to-next table), `PlayerName`, `AllocatedPassiveNodeIds`
- Area: `AreaHash`, `AreaLevel`, `AreaCode` (internal, e.g. `G1_town`), `LeagueName` (HC prefix distinguishes), `Landmarks`, `Terrain` (walkable bitmap)
- Entities: `Poe2Live.Entities` → `EntityDot{Id, Category, Metadata, Rarity, HpCur/Max, Grid, World, Reaction, ItemName, ItemArt, ItemIdentified, ItemAffixes, Poi, IconComplete, Mods}`; `Buffs`, `Targetable`, `Chest`, `Shrine`, `Transitionable`, `TriggerableBlockage`, `Monolith` state
- Drops: fully instrumented (WorldItem → inner item entity for name/rarity/art; `DropTimeline` persists to disk, ring-capped ~1000)
- Session: `SessionTracker` tracks lifetime session counters (deaths with respawn-latch, kills by rarity, XP/hour ring, maps/hour, XP efficiency); per-zone counters reset on zone entry; `KillTracker` observes unique kills
- Boss catalog: `BossEncounterCatalog.Shared` — `matchMetadata` + `AreaCode` inference
- Dashboard surface: `/state` (session + player vitals + area, character name STRIPPED for privacy), `/stream` SSE (player + entities + monoliths + polylines at ~30Hz), `/api/map` (walkable bitmap + dims), `/api/drops`, `/api/atlas` (region-scoped), `/api/icons`, `/api/settings` (Gold-gated writes)
- Web API: custom `HttpListener` handler routing (NOT ASP.NET Core minimal APIs)

**NOT available (features that need this go RED or need a discovery arc):**
- Currency stack counts (offset `StackComponent+0x18` defined but no reader wrapper)
- Market pricing / PriceBook / poe.ninja / trade API (all Ex values hardcoded to 0)
- Per-kill event stream (only aggregate rarity counters in `/state.session`)
- Level-up edge events (SessionTracker reads PlayerLevel but doesn't emit on transition)
- Gear/passive-tree diff events (passive-node IDs read; no name lookup, no change stream)
- "Map complete" flag (game exposes no signal; heuristic required)
- Death event details (only `deaths` and `deathsThisZone` counters, no location/killer/timestamp)
- Ascendancy points, skill points, respec points

## v0.35 — Signature Palette

**Narrative.** Gold announces itself with a cosmetic win everyone sees on first login, plus a streamer freebie so Gold shows on-camera the same week it ships.

**Flagship: Curated Theme Pack** (M, GREEN, ~350-450 LOC mostly CSS)
- 8 named palettes as CSS blocks under `body[data-palette="<name>"]` in `src/POE2Radar.Overlay/Web/Assets/dashboard.css`
- Each palette overrides: surface / panel / panel2 / accent / text / border / danger / muted / vitals-bar tints / chart series
- Register names in the `dashboardPalette <select>`; `RadarSettings.DashboardPalette` already persists, `dashboard.js:2134-2145` already applies + gates behind supporter code
- Contrast QA sweep against `map.css`: entity dots, walkable terrain, path overlays, vitals-bar tints
- **Entitlement:** kalguuran + terminal STAY at existing supporter tier (no regression); 8 new palettes are Gold-only

**Engineering delighter: Stream-Safe Overlay Mode** (M/L, GREEN, ~400-500 LOC)
- New browser-source route `/obs?mode=safe&delay=<sec>` renders same view as `map.js` through a client-side redaction pipeline
- Pipeline: (1) delay ring buffer keyed to frame timestamps, (2) zone-name masking to `<area>`, (3) hideout coord blur (position snapped to zone-center for hideout AreaCode), (4) optional entity-name fog
- Consumes existing `/stream` + `/state` + `/api/map`; no new memory reach
- Persist settings in `RadarSettings`
- **Meat behind the M rating**: full/delta SSE interleave + zone-reset correctness in the ring buffer — spec these edge cases explicitly before code

**Small delighter: Themed Session Recap PNG** (S, ~40 LOC)
- Palette color values (accent / panel / text / border as hex) piped from `RadarSettings.DashboardPalette` into the recap renderer via a small `PaletteColorSet` record — recap renderer reads hex, not CSS vars (raster context)
- Recap header + accent bar + separator lines pick from that record
- Shareable recap PNG matches the streamer's dashboard skin

**Ship checklist**
- [ ] 8 palettes designed with intentional coherence (bg/panel/accent/text/vitals/chart) — DESIGN effort is load-bearing, not code effort
- [ ] Contrast QA against map.css verified on all 8
- [ ] Stream-Safe delay buffer handles zone-reset + full/delta interleave
- [ ] Hideout coord blur unit-tested (AreaCode prefix match)
- [ ] Themed Recap PNG renders correctly for each palette

## v0.36 — Your Icons on the Map

**Narrative.** Second "pack" beat. Themes made the chrome yours; icons make the overlay itself yours. Rendering-layer + config-loader change — no new game reach.

**Flagship: Custom Entity Icons** (M, GREEN, ~500-600 LOC)
- New `IconRegistry` in Core watching `config/icons/` via `FileSystemWatcher`; atomic snapshot (immutable dict rebuilt on fire; renderer reads at 30Hz from a different thread)
- Mapping precedence: `metadata-glob > category+rarity > category > default`
- OverlayRenderer: swap dot → textured quad; "tint by rarity" toggle preserves rarity color channel
- Precedent: `/api/icons` already ships an SVG icon library the dashboard consumes — established pattern

**Delighter: Bundled Gold starter icon pack** (art + ~50 LOC loader)
- Original / CC0 / CC-BY art at 2-3 mip sizes, pre-rasterized PNG, shipped as one texture atlas
- Feature has visible value on first launch without any user authoring
- **Matched to v0.35 palettes for "themed set" feel** — each starter icon tuned for the 8 palettes' contrast
- **⚠ Critical-path decision below (Open Items §4)** — sourcing / licensing / attribution must be resolved BEFORE v0.36 code lands

**Delighter: Web map.js icon parity** (~120 LOC)
- New `GET /api/user-icons` manifest (list + bytes as data-URIs)
- `map.js` swaps circles for `<img>` using the same precedence table
- Both surfaces stay in sync

**Decisions locked in this spec (previously open items)**
- **PNG-only for user files in v1.** SVG deferred. Sidesteps SkiaSharp thread-safety on the D2D/GDI overlay render path. Bumps to L bracket + SkiaSharp dependency review if reintroduced.

**Ship checklist**
- [ ] IconRegistry atomic-snapshot semantics verified under concurrent read + FileSystemWatcher fire
- [ ] Precedence table documented in a config schema example
- [ ] Texture atlas size fits 800-entity SSE cap per-frame budget
- [ ] Starter pack copyright cleared + attribution file shipped
- [ ] Web map.js parity: manifest + data-URI rendering

## v0.37 — Book of the Exile

**Narrative.** Arc pivots from personalization to chronicle. Every character now keeps a persistent, per-character, on-disk event journal — rendered as a dated "book" spread. Extends the `DropTimeline` persistence pattern (proven in the codebase) to a wider event schema.

**Flagship: Character Codex** (L, YELLOW, ~900-1100 LOC)
- Per-character JSONL ring at `config/codex/<character>.json` (~5000 entries, evicting oldest)
- Mirrors `DropTimeline`'s load-on-construct + append + debounced-flush pattern
- **Four event sources, all wired from signals already in memory:**
  1. **LEVEL-UP** — `SessionTracker` already reads `PlayerLevel` for xpEfficiency; emit `{kind:"level", level, zone, ts}` on delta
  2. **BOSS-KILL** — hook `KillTracker`'s unique-death path; sample dying `EntityDot.Metadata`; lookup `BossEncounterCatalog.Shared.matchMetadata` (or current `AreaCode` for `MapUberBoss_*` zones); emit `{kind:"boss", key, label, zone, ts}` **only on catalog hit** (unknown uniques dropped silently, not logged as "unknown boss")
  3. **DEATH** — extend existing `_awaitingRespawn` / `_hpObservedAboveZero` edge that increments `_deaths`; emit `{kind:"death", zone, areaLevel, playerLevel, ts}` at the same edge
  4. **NOTABLE DROP** — subscribe to `DropTimeline.Record`; forward `rarity==Unique` entries (plus first-seen-itemArt per character) as `{kind:"drop", name, rarity, art, zone, ts}`
- New `GET /api/codex?character=<name>` in `ApiServer` — **loopback-Host-gated** (character name never leaks into `/state`; matches existing privacy posture)
- New dashboard `Codex` tab renders per-day-grouped scrollable "book" spread with per-kind icons

**Delighter: Per-day grouping + jump-to-date navigation** (~80 LOC dashboard.js)
- Collapsible date headers, jump-to-date, "today / yesterday / this week" shortcuts

**Delighter: Codex filter chips** (~40 LOC)
- Client-side filter by kind (level / boss / death / drop), remembered per-tab-open

**Discovery beads (blocking prereqs)**
- **BEAD 1 — boss-kill attribution validation.** Manual playtest across ~10 known bosses + ~30 non-boss uniques (Breach uniques, rogue exiles, rare-modifier variants). Confirm `BossEncounterCatalog.matchMetadata` + `AreaCode` inference produces zero false positives on the non-boss set. **Must ship before persistence goes live** — a false-positive persisted forever is worse than no attribution.
- **BEAD 2 — `SessionEventLog` subsystem scaffold.** Mirror `DropTimeline`: load-on-construct + append + debounced flush, ring-capped, corrupt-file → fresh start. Include character-name stability gate (`PlayerName` non-empty AND stable for N ticks before opening a file) to defeat character-swap flicker on login.

**Decisions locked in this spec (previously open items)**
- **Storage:** per-character files (`config/codex/<name>.json`). Matches "book" framing; character-name is stable and DropTimeline already tags entries with character.
- **Boss attribution:** strict allowlist for v0.37. Debug bucket for unknown-uniques deferred to a v0.37.x point release if attribution proves too narrow in real play.
- **Drop dedup key:** `itemArt` basename (what the memory read produces; stable across renames).

**Ship checklist**
- [ ] BEAD 1 delivered — zero false positives on 30-entity non-boss sample
- [ ] BEAD 2 delivered — `SessionEventLog` handles all 4 event kinds, corrupt-file recovery tested
- [ ] Character-name stability gate defeats login flicker (unit test)
- [ ] `/api/codex` loopback-Host gate enforced (integration test — non-loopback returns 403)
- [ ] `/state` still strips character name (regression test)
- [ ] Book UI renders ~5000-entry file without jank

## v0.38 — The Forge

**Narrative.** "Pack first, forge later" pays off. Users have curated themes (v0.35), themed icons (v0.36), and a chronicle (v0.37). v0.38 lets them author their own palettes with live preview and share them by code.

**Flagship: Color Forge** (M, GREEN, ~400-600 LOC)
- Full color designer: every CSS custom-property in the palette pipeline is user-authorable via HSL / hex sliders + text input
- Live preview: sample dashboard panel (kill card, drop card, vitals bar, chart tile, button, tooltip) re-renders on each variable change without page reload
- Save named presets to `config/palettes/<name>.json`; user's saved presets appear in the same `dashboardPalette <select>` alongside the 8 v0.35 palettes and kalguuran/terminal
- New `GET+POST /api/palettes` (loopback-Host-gated for writes, following the existing settings-write pattern)

**Delighter: Theme code sharing** (~80 LOC)
- Compact serialization: palette JSON → base64 → 6-8-char human-readable prefix (e.g. `RUNE-abc123-def456`)
- Copy/paste box on the Forge tab; paste import writes to `config/palettes/imported-<hash>.json` and applies live
- No egress — pure local encode/decode

**Delighter: Preset gallery** (~60 LOC + JSON presets)
- The 8 v0.35 palettes + kalguuran/terminal appear as "starting points"
- One-click "clone as editable" → new preset with editable name, seeded from the source palette's values
- Teaches the Forge UX by giving users known-good starting points

**Ship checklist**
- [ ] Live preview updates every variable independently without full re-render jank
- [ ] Preset save round-trips (write → reload → same colors)
- [ ] Share code encode/decode is stable (paste-your-own-export gives you back your palette)
- [ ] Palette name collisions handled (never overwrite silently — always suffix or prompt)
- [ ] All Forge writes go through the loopback-Host gate

## Cut list

| Cut | Verdict | Reason | Natural future home |
|---|---|---|---|
| Session Wrapped | RED (XL) | Currency reader, PriceBook, market API — three missing subsystems. Any poe.ninja / trade call is unapproved outbound egress. Degraded version overlaps too heavily with existing free Recap PNG. | Only after currency-reader + PriceBook infra arcs land. Multi-quarter deferral. |
| Layout Designer | GREEN (XL) | ~900 LOC drag+snap+hide+hotkey+resize+opacity+persistence eats two release slots. Every future panel must opt into `data-panel-id`/drag-handle/min-size contract or crash the drag math — lifetime maintenance tax. | v0.41 "Dashboard Studio" standalone release. |
| Rules Engine — Per-Entity Overrides | GREEN (L) | Deferred from v0.38 slot (LO chose narrower Color Forge). Consolidates 5 existing rule surfaces (display-rules/hidden/landmarks/affix-nameplates/buff-nameplates); would open its own composability arc. | **v0.39 flagship** — head of the next arc. |
| Cartographer (movement heatmap + route replay) | GREEN (M) | Technically clean; no room in this arc without a sixth flagship. Uses the position stream nobody else has → strongest technical moat. | v0.40 "Advanced Analytics" flagship. |
| Timeline Detective | YELLOW (L) | Thematically overlaps Codex (both scrubbable dated event streams). Codex wins the slot — per-character has more repeat-visit value than per-session. | v0.40 co-headliner if Cartographer needs a partner. |
| Personal Bests Shrine | YELLOW (L) | Two YELLOW-L features in one arc (Codex is one) is the ceiling for prereq load. Overlaps Codex ("persistent per-character journal" framing). | v0.40 Advanced Analytics. |
| Atlas Conqueror | YELLOW (L) | Reuses same boss-kill attribution bead as Codex, so should land AFTER Codex validates it. Region-scoped vs "entire atlas" scope call needs a data-source decision. | v0.40 — reuses validated attribution + adds map-entries log + atlas UI. |
| Death-cam Replay | YELLOW (M) | Streamer-focused; arc chose personalization+chronicle+forge as spine over streamer beat. | v0.39+ "Streamer Kit" with Stream-Safe (partially co-shipped in v0.35) + Brand Kit. |
| Channel Brand Kit | YELLOW (M) | Font-licensing gray area + `/obs` cel endpoint doesn't exist yet + SVG rasterizer in overlay is missing. Curating 6-8 open-source families is its own arc. | v0.39+ Streamer Kit. |

## Open items (LO decision or discovery-bead required)

1. **Icon starter-pack sourcing + copyright (BLOCKING v0.36).** Original art or CC0/CC-BY licensed. Who authors/sources? Options:
   - (a) LO commissions/authors → schedule slip risk if art isn't ready when code is
   - (b) Curate CC0 icon set (Game Icons.net, Kenney assets) → licensing clean, less bespoke
   - (c) Defer starter pack out of v0.36 — ship icons BYO-only → v0.36 has no visible-value-on-day-one delighter; risks Gold sign-ups feeling empty until users author their own
   - **Recommendation:** (b) — curated CC0 with attribution file. Ship (a)-authored pack as v0.36.x delighter if LO wants bespoke.

2. **Streamer-Kit v0.39+ roadmap direction (POST-ARC).** LO should sanity-check that cut list's implied v0.39+ arc (Rules Engine v0.39 head-of-next-arc, then Streamer Kit + Advanced Analytics + Dashboard Studio across v0.40-v0.41) is the right direction before committing to this 4-release cut list.

3. **Codex JSONL 5000-entry cap review.** Ring evicts oldest. A very-long-lived character (200+ hours) might overflow interesting early events (first Act 1 boss). Alternative: no cap on level/boss events (rare, small), cap only on drops (frequent, large). Recommend LO decide after seeing the file grow in real play; v1 ships flat 5000 cap and the eviction logic is one function to swap.

4. **Stream-Safe defaults.** What's the shipping default for `delay=<sec>`? Recommendation: 30s (typical stream-snipe reaction window; matches Twitch chat latency). Zone-masking on by default, hideout blur on by default, entity-name fog OFF by default (too aggressive for casual streamers). LO can confirm or override before v0.35 spec goes to plans.

## Integration with existing perk suite

Yesterday's suite was 15 perks across 3 releases with 2 confirmed Gold anchors (Custom Entity Icons v0.36, Character Codex v0.37) alongside existing Cloud Settings Sync (v0.35) and Extended Drop Timeline (v0.36/v0.37).

This arc **replaces** the "confirmed Gold anchors" portion of yesterday's plan with a fully-scoped 4-release arc. It does NOT touch the other perks in the yesterday suite (Cloud Settings Sync, Extended Drop Timeline, etc.) — those stay wherever they were planned. If any conflict surfaces during v0.35 spec-writing (e.g. dashboard-tab real-estate), we resolve it then.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| v0.36 icon starter-pack blocks the release | Decision resolved before spec goes to plans (Open Item §1). Fallback (b) can ship on any timeline. |
| v0.37 boss-attribution false positives persist forever in the codex | BEAD 1 gates release; strict allowlist for v1; debug-unknown-uniques bucket held for v0.37.x |
| v0.37 character-name flicker on login corrupts the wrong file | Character-name stability gate (BEAD 2) — file not opened until name stable for N ticks |
| Stream-Safe delay buffer mishandles SSE full/delta interleave | Explicit spec on zone-reset behavior + full/delta merging in v0.35 spec |
| Color Forge writes proliferate `config/palettes/*.json` without cleanup | Preset delete UX in the Forge tab; name-collision guard prevents silent overwrites |
| Palette v0.35 contrast breaks map.css entity dots or vitals bars | Contrast QA sweep in the ship checklist; per-palette test render before merge |
| Existing supporters lose kalguuran/terminal on the tier-consolidation path | Entitlement policy is explicit: existing supporter palettes stay at existing supporter tier; only the new 8 are Gold-gated. Regression test enforces (a supporter-tier code still selects kalguuran/terminal successfully). |

## Signals of success (per release)

- **v0.35** — At least 5 of the 8 palettes get real-user selection within the first week; no map.css contrast regressions; Stream-Safe delivers redacted output that matches the delay setting under a zone transition.
- **v0.36** — Starter icon pack renders in both overlay + web map without user config; at least 3 Gold users author custom icons within a month; icon precedence table produces correct render for known metadata patterns (unit-tested).
- **v0.37** — Codex opens without lag on a 5000-entry file; per-day grouping renders correctly across timezone edge cases; zero false-positive boss entries observed in first month of real play.
- **v0.38** — At least one shared theme code circulates in the community Discord within the first week; Forge Live-preview handles rapid variable changes without dropping frames; saved presets round-trip cleanly.

## References

- **Signal audit (full):** `scratchpad/gold-tier-signal-audit-w25celc59.json` (workflow run 2026-07-15)
- **Yesterday's brainstorm output:** `scratchpad/gold-tier-brainstorm-full.json`
- **Existing palette pattern:** [dashboard.css:203](../../src/POE2Radar.Overlay/Web/Assets/dashboard.css#L203)
- **DropTimeline persistence pattern (blueprint for SessionEventLog):** search `src/POE2Radar.Core` for `DropTimeline`
- **BossEncounterCatalog:** search `src/POE2Radar.Core` for `BossEncounterCatalog.Shared`
- **ApiServer routing (custom HttpListener, NOT ASP.NET Core minimal APIs):** `src/POE2Radar.Overlay/Web/ApiServer.cs`
- **RadarSettings.DashboardPalette + Gold gate:** `dashboard.js:2134-2145`
