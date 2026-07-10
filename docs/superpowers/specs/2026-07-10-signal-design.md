# POE2GPS v0.23 "Signal" — Design Spec

**Date:** 2026-07-10
**Ship target:** v0.23.0

## Goal

Fix three latent bugs that make the app feel broken (`/map` never rendered terrain, alert volume slider is a no-op, preload panel is unmanageable in busy zones) and turn Campaign Probe from a silent internal system into a first-class opt-out community-data feature that piggybacks its uploads onto every existing Contribute click.

## Theme

**Signal.** Data flowing where it should — probe traces reaching the community pool, alert volume actually reaching the ears, terrain actually reaching the map. A polish drop that fixes what's been quietly broken.

## Motivation

- User reports (LO): black `/map` in every zone, volume slider does nothing, preload panel gets cluttered as encounters resolve.
- Internal state: Campaign Probe has been running silently for 24 hours (since v0.22 merged 2026-07-09) accumulating JSONL locally on user machines with no user-facing communication and no upload path deployed. That's a policy debt to resolve before the sample size grows further.

## Non-goals

- PMS-14 in-game verification pass for UI-tree observers (`npc_dialogue_*`, `quest_reward_selected`). Deferred to its own drop when LO has desk time. This drop announces the probe with a note that dialogue/reward observers are pending.
- R5 Waygate atlas-landmark cross-path detector (POE2GPS-9ni). Still parked until AtlasRefresh.
- Preload panel hide-on-death. This drop implements hide-on-spawn per LO's semantics call. Death detection is a follow-up if usage shows spawn alone doesn't feel right.
- Wire-format restructuring of `/stream` SSE. The `/map` fix ports on the client, preserving the additive-only v0.20 wire-format constraint.

## Architecture at a glance

Six shipped items + one manual deploy step:

1. **`/map` terrain fix** — client-side coercion at `map.js:242`. Compare `data.areaHash.toString(16)` (client-computed hex) against the SSE hex-string `area`. Regression test locks the wire-format contract on both sides.
2. **Alert volume slider fix** — client-side coercion at `DashboardHtml.cs:1278`. Extend the `wireSettings()` "number" branch to also match `el.type === 'range'`. Existing server-side `TryInt` guard is left as-is.
3. **Preload panel collapse toggle** — new `PreloadPanelCollapsed: bool` (default false) on `RadarSettings`, mirrored through `RenderContext`, caret + hit-rect in `DrawPreloadPanel`, `"preload-collapse"` action in `OnOverlayClick`.
4. **Preload catalog entity binding** — extend `PreloadCatalog` entries with an optional `SpawnEntityMetadata: string?` field (the metadata substring or path prefix that marks the entity as spawned in the entity list). Backfill entries for the categories where spawn detection makes sense (Boss, Uniques, Rituals). Leave unset for categories where spawn ≠ completion (Shrines, Chests).
5. **Preload hide-on-spawn** — in the `WorldTick` path, after `_entities` is populated, mark any `PreloadHit` whose `SpawnEntityMetadata` matches an entity's metadata as spawned. New `Spawned: bool` field on `PreloadHit`. Render skips rows where `Spawned == true`. Panel height recomputes accordingly (feeds cleanly into item 3's collapse layout).
6. **Contribute auto-piggyback + probe announcement** — `#eaContribute`, `#bnContribute`, `#prContribute` handlers each fire-and-forget `POST /api/contribute-trace` after their primary POST succeeds, gated on the cached `enableCampaignProbe` setting. `#tpContribute` stays with a new subtitle `auto-fires with atlas contributions`. Themed CHANGELOG entry documents the probe: what it collects, opt-out path, upload endpoint, PMS-14 pending status. README gets a one-line pointer to the settings toggle.

**Manual step (LO, pre-tag):**
7. **Cloudflare Worker `/submit-trace` deploy.** `wrangler kv:namespace create RATE_KV` → replace `TODO(pms-13-kv-id)` sentinel in `wrangler.toml` → `wrangler deploy`. If this doesn't ship before the tag, piggybacked traces will 404 at the Worker and the announcement lies. `docs/pending-manual-steps.md` gets PMS-15 tracking this.

## Wire-format contract for `/map` (locked)

To prevent future drift:

- Server `/api/map` emits `areaHash` as JSON number (canonical form is the underlying `uint`).
- Server `/stream` SSE emits `area` as `s.AreaHash.ToString("x")` (canonical form is hex string).
- Client `map.js` treats the SSE `area` as the source of truth for identity comparison. When comparing against `/api/map` payloads, the client MUST convert `data.areaHash` to hex via `.toString(16)` before comparison.

A test on the C# side asserts both formats independently. A test on the client side (or a code comment naming the invariant) locks the coercion at the compare site.

## Global constraints

- **Zero memory writes.** No new `Marshal.Write*` / `MemoryReader.Write*` / `SendInput` / `keybd_event` / `mouse_event` / `WriteProcessMemory` / `VirtualProtect`.
- **v0.20 wire-format additive-only.** `/stream` SSE keys unchanged. `/api/settings` gets no key renames; new keys additive only.
- **No `TODO`/`FIXME`/`HACK`/`XXX` in new code.**
- **No `superpowers/`, `.superpowers/`, `docs/superpowers/` paths in shipped code, README, CHANGELOG, in-app strings, or Discord post.**
- **No mention of Sikaka / GameHelper / upstream repo names.** Attribution-hygiene sweep from the pipeline drill (2026-07-10) already stripped those; new code must not reintroduce.
- **Compliance gates GREEN** at spec time and post-drop: `scripts/compliance-gate.ps1`, `scripts/scrub-strings.ps1 -SelfTest`, `scripts/attribution-sentinel-gate.ps1`.
- **One clean commit per task.**
- **All tests via `dotnet test` from repo root.**

## Non-negotiables (per-item)

- **`/map` fix must include a test that exercises the wire mismatch.** Either a C# test that asserts both `/api/map` payload shape AND `/stream` sample shape use the same-key identity when coerced, OR a comment at `map.js:242` naming the invariant so a future refactor doesn't drift back. Both preferred.
- **Preload collapse default is `false`** (unlike `MonolithPanelCollapsed=true`). Preload is a "look at this" panel; users should see it first.
- **Preload hide-on-spawn must be per-category opt-in.** `PreloadCatalog` entries without a `SpawnEntityMetadata` never hide.
- **`#tpContribute` stays visible.** LO's call. Subtitle `"auto-fires with atlas contributions"` (or equivalent, ≤50 chars) so users don't think it's redundant.
- **Probe announcement mentions opt-out plainly.** The word "opt-out" or "off by default" comparison must appear so users who don't want participation know the toggle.
- **Trace piggyback is fire-and-forget.** Client does not wait on `/api/contribute-trace` response; failure at the trace endpoint must not block or degrade the primary Contribute UX.

## PMS additions

- **PMS-15 (new, Active):** Cloudflare Worker `/submit-trace` deploy step. Blocks v0.23 tag. Rows: create KV namespace, replace `wrangler.toml` sentinel, `wrangler deploy`, smoke-verify a manual `POST` reaches the deployed worker.
- **PMS-14 (unchanged, Active):** UI-tree observer live verification. Not folded into v0.23.

## CHANGELOG shape (draft)

```markdown
## [0.23.0] — 2026-07-XX "Signal"

### Added — 📡 **Signal**

- 📈 **Campaign Probe (announcement).** POE2GPS ships an opt-out anonymized zone-traversal probe that has been quietly running since v0.22 to help build a shared campaign atlas. …
- 🗂️ **Preload panel: collapse toggle + hide-on-spawn.** …
- 🔊 **Alert volume slider fix.** …
- 🗺️ **`/map`: terrain renders now.** …
- 📤 **Trace uploads piggyback on Contribute clicks.** …
```

Draft body filled during the task plan.

## Approval

LO green-lit on 2026-07-10:
- Scope: v0.23 combined per LO's "do v0.23 option" answer + /map folded in per LO's follow-up.
- Theme: "Signal" per AskUserQuestion answer.
- `#tpContribute`: keep + subtitle per AskUserQuestion answer.
- Preload completion semantics: spawn (entity appears in list), not spawn+die.
- PMS-14 deferred to its own drop.
