# POE2GPS Campaign Probe — Design Spec

**Date:** 2026-07-08
**Prior release context:** v0.21 "Guided Campaign" is on main pending tag (blocked on PMS-12 + PMS-13). This feature ships parallel to that tag routine, not blocking it — new subsystem, no v0.21 surface changes.

---

## 1. Goal

Bake a **campaign-data probe** into POE2GPS that captures anonymized player zone traversals during campaign play. Data is written to a local JSONL file the user inspects before sharing. Shared traces flow through the existing v0.21 sibling-route Contribute pipeline into a public data pool consumed by POE2GPS's Campaign Director (and any other tool that wants to read the public pool).

The pool is the training substrate for POE2GPS's Guided Campaign + Objective Director — hand-authoring 30+ zones' worth of route data is tedious and drifts as GGG rebalances, so real player traversals do the work.

## 2. Non-negotiables

- **Zero PII.** No character name, no account, no login-related identifiers, no world coords outside the game grid. Text values (NPC names, dialogue) are hashed to 16-char sha before write.
- **Anonymous by construction.** `install_uuid` is a random UUID minted on first install and stored in `RadarSettings.ProbeInstallId`; `boot_id` regenerates per process. Neither is tied to the user's identity or the machine's identity.
- **Read-only over game memory.** No memory writes. No new offset writes. All reads follow the existing `Poe2Live` chain patterns.
- **Zero-cost-when-off.** With `EnableCampaignProbe = false`, `CampaignProbe.Tick(...)` short-circuits before any work. Spy test enforces zero writes + zero allocations per tick when disabled.
- **Local capture only.** No network I/O in v1. Users share by clicking Contribute (the existing v0.21 pipeline). Nothing uploads without an explicit click.
- **Same schema forever + all 12 events live at ship** (revised 2026-07-08 after upstream-offset extraction from `imkk000/poe2-offsets` — see `scratchpad/campaign-probe-offsets.md`). Previously two-bucket A/B split; now collapsed. All 12 event types populate live. Two events (`npc_dialogue_option_selected`, `quest_reward_selected`) use UI-tree signature detection instead of raw panel offsets — same technique POE2GPS already uses for landmarks + atlas nodes. PMS-14 downgrades from a "5-chain Research probe (45-60 min)" to a "10-15 min in-game verification pass" that confirms the 6 previously-Bucket-B events emit plausible values. The `probe_capability` envelope field stays for forward-compat but reads `"live"` on all 12 events at ship.
- **Default ON.** LO's design decision. Onboarding toast fires exactly once explaining what the probe captures and how Contribute works.

## 3. Event schema

All 12 event types from LO's brief ship in v1. Every record carries a common envelope + event-specific fields.

**Envelope (every record):**

| Field | Type | Notes |
|---|---|---|
| `ts_epoch_ms` | int64 | `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` |
| `install_uuid` | string (36) | stable across boots for this install |
| `boot_id` | string (36) | fresh per process |
| `event_type` | string | one of the 12 below |
| `probe_capability` | string | `"live"` or `"v0.22_pending"` |
| `schema_version` | int | starts at 1; only bumps on breaking changes |
| `act_hint` | string | `"act1".."act6"`, `"act1_cruel".."act6_cruel"`, or `"unknown"` |
| `area_name` | string | current zone display name (from `Poe2Live.WorldArea`) |

**Event types + Bucket assignment:**

| Event | Bucket | Extra fields |
|---|---|---|
| `zone_entered` | A | `area_level: int, area_id_hash: string, is_town: bool, is_hideout: bool, player_world_pos: {x,y}` |
| `area_transition_used` | A | `source_area: string, destination_area: string, transition_entity_metadata_path: string, transition_world_pos: {x,y}` |
| `boss_encountered` | A | `boss_metadata_path: string, boss_display_name: string, boss_world_pos: {x,y}, is_first_encounter: bool` |
| `checkpoint_touched` | A | `checkpoint_metadata_path: string, world_pos: {x,y}` |
| `waypoint_unlocked` | A | `waypoint_entity_metadata_path: string, world_pos: {x,y}` |
| `player_death` | A | `last_damage_source_metadata_path: string \| null, character_level: int` |
| `waypoint_travel` | B | `source_area: string, destination_area: string, waypoint_menu_row_index: int` |
| `npc_dialogue_started` | B | `npc_name_hash: string(16), npc_metadata_path: string, npc_world_pos: {x,y}, dialogue_text_hash: string(16), option_count: int` |
| `npc_dialogue_option_selected` | B | `npc_name_hash: string(16), option_index: int, option_text_hash: string(16), remaining_option_count: int` |
| `quest_reward_selected` | B | `reward_metadata_path: string, reward_display_name_hash: string(16), offer_index: int, total_offers: int, was_skipped: bool` |
| `passive_allocated` | B | `node_id: int, node_display_name_hash: string(16), character_level: int` |
| `level_up` | B | `new_level: int, xp_at_level: int64, area_name_when_leveled: string` |

**Bucket-B stub emission (pre-PMS-14):** the record still fires but every field beyond envelope + `probe_capability: "v0.22_pending"` is either omitted or set to a documented sentinel. E.g. a pre-PMS-14 `level_up` fires on a heuristic (character level in entity list bumped) but `xp_at_level` is `null` and `area_name_when_leveled` reads from current `Poe2Live.WorldArea`.

## 4. Architecture

### 4.1 Core (`src/POE2Radar.Core/Campaign/Probe/`)

```
CampaignProbe.cs        — orchestrator; owns diff-observers; Tick(snapshot)
EventRecord.cs          — 12 record structs + a discriminated union serializer
EventWriter.cs          — JSONL sink; per-boot file rotation; async-flush discipline
AnonymizationHelpers.cs — HashText16(string) → 16 chars of sha256-hex; NewInstallUuid()
```

- **`CampaignProbe.Tick(worldSnap)`** — called from `RadarApp.CampaignReconcile` after `WorldStateAdapter.Refresh` (world thread, once per tick, ~30 Hz). Gated on `_settings.EnableCampaignProbe`.
- **Diff-observer pattern:** last-tick state cache + this-tick state → event emissions. Same shape as `RouteCursor.AdvanceIfSatisfied` from v0.21 — proven world-thread ownership discipline.
- **File writer:** append-only, buffered via `StreamWriter` with `AutoFlush = false`; flush every 32 events or every 1s (whichever first). Never blocks the world thread — writes queue to a background thread via `Channel<EventRecord>`.

### 4.2 Overlay (`src/POE2Radar.Overlay/`)

- **`Config/RadarSettings.cs`**: add three fields
  - `EnableCampaignProbe: bool = true`
  - `ProbeInstallId: string = ""` — auto-populated on first launch via `AnonymizationHelpers.NewInstallUuid()`
  - `ProbeOnboardingSeen: bool = false`
- **`RadarApp.cs`**:
  - Construct `CampaignProbe` + `EventWriter` at startup (unconditionally — one object graph, matching v0.21 `WorldStateAdapter` pattern)
  - Wire `CampaignProbe.Tick(worldSnap)` into `CampaignReconcile` gated on `EnableCampaignProbe`
  - Auto-populate `ProbeInstallId` on first launch if empty; migrate via `SettingsMigrator` (`AppliedMigrations` list gets a `"probe_install_id_v1"` entry)
- **`Web/DashboardHtml.cs`**:
  - New Settings row: "Campaign trace probe (helps POE2GPS's Campaign Director learn campaign routes from your play)" with toggle bound to `EnableCampaignProbe`
  - New Settings row: "Reset trace session id" button that regenerates `ProbeInstallId`
  - New Campaign-panel Contribute button: **"Contribute trace"** — POSTs the current JSONL file (or the most recent complete boot's file) to `/api/contribute-trace`, hidden if probe disabled
  - One-shot Dashboard toast: fires when `ProbeOnboardingSeen == false && EnableCampaignProbe == true`; sets `ProbeOnboardingSeen = true` on dismiss
- **`Web/ApiServer.cs`**:
  - New `/api/contribute-trace` handler at the same layer as `/api/contribute-{atlas,buffs,preload}`
  - Packs the trace JSONL from disk, forwards to Worker's `/submit-trace` via `SiblingContributeUrl(url, "trace")` (extends the v0.21 helper — adds a fourth pattern-match)

### 4.3 Worker

`cloudflare-worker/worker.js`:
- New sibling route `POST /submit-trace` — same middleware pipeline as atlas/buffs/preload (NFKD leet-fold profanity filter is a no-op for trace payloads but stays for defense-in-depth against any user-typed field; KV rate limit; gh dispatch)
- Payload shape: `{ install_uuid: string, boot_id: string, event_count: int, jsonl_gzip_b64: string }` — server unzips, validates schema-version, posts an Issue with the JSONL attached as a code block (or a Gist link if >65 KB per GitHub's Issue body limit)
- Issue label: `community-pack` + `trace`
- `routeFor` gains a fifth case; `/submit` legacy alias unchanged; tests updated

## 5. Contribute pipeline

Extends the v0.21 pattern. Fourth pack type in `merge_community.py` — no fold into any C# catalog (traces aren't catalog seeds, they're raw data). Instead, `merge_community.py --traces` writes the accumulated traces into a public repo folder `resources/campaign-traces/<install_uuid>/<boot_epoch_ms>.jsonl` that any consumer can `git clone` from.

Consumers of the public pool:
- **POE2GPS's own Campaign Director** — future task; will read the public pool to enrich the syrairc-seeded route data
- **Any third-party tool** — the pool is public; no gate

## 6. Onboarding

**First-launch trigger:** on Dashboard load, if `EnableCampaignProbe == true && ProbeOnboardingSeen == false`, render a one-shot toast (matches the v0.21 `showToast` helper Task 12 shipped).

**Toast copy (final):**
> **Campaign trace probe is on.**
> Your zone traversals get logged to a local file (nothing uploads). One-click **Contribute trace** in the Campaign panel shares a session so POE2GPS's Campaign Director gets smarter with more players' routes. The shared pool is public.
> Turn off in ⚙️ Settings → Campaign trace probe.
> [Got it]

`Got it` sets `ProbeOnboardingSeen = true`. Toast never fires again.

## 7. PMS-14 Research probe

**New PMS entry** in `docs/pending-manual-steps.md`. LO runs a single Research session that captures FIVE offset chains in one play window:

1. `IngameState.Data.ServerData.QuestFlags` — `Dictionary<QuestFlag, bool>` root pointer (already noted in v0.21 spec §6 as PMS-4-adjacent)
2. `IngameState.Data.ServerData.NpcDialog` — dialog root + open flag + option list
3. `Targetable` component byte layout on `EntityDot` (transition detection improvement)
4. `IngameState.Data.ServerData.PassiveTreeState` — allocated node bitset
5. `IngameState.Data.ServerData.Experience` — int64 (also unlocks Long List #34 XP/hour)
6. `IngameState.Data.ServerData.QuestRewardOffers` — quest-reward UI list

`POE2Radar.Research --campaign-probe-offsets` — new probe mode shipped in Track A. Prints the discovered offsets in a copy-paste-into-`Poe2Offsets.cs` format.

Post-Research: Track B (~3 tasks) swaps the six bucket-B events from stubs to live reads. Schema unchanged.

## 8. Anti-abuse posture

Traces are public data. Bad actors could:
1. **Fake trace data** to poison the pool → downstream consumers filter by trace plausibility (event ordering: `zone_entered` must precede `boss_encountered` in same area; `passive_allocated.character_level` must be monotone; etc.)
2. **Fingerprinting via `install_uuid`** → users can regenerate the uuid via Settings; anyone worried about correlation can reset before contributing

CF Worker rate limit (v0.21's 5/60s per IP) applies to `/submit-trace` — an attacker can't blast the pool with faked traces from one IP.

## 9. Tests

- **`EventRecordTests.cs`** — round-trip serialize/deserialize for each of the 12 event types; snake_case field names match brief spec byte-for-byte
- **`AnonymizationHelpersTests.cs`** — `HashText16` deterministic + 16 chars + hex; `NewInstallUuid` is a valid UUID v4 with the right entropy
- **`EventWriterTests.cs`** — file rotation on new boot_id; async-flush respects batch size + timer; opt-off = zero file writes across 1000 ticks
- **`CampaignProbeTests.cs`** — diff-observer emits `zone_entered` on area change; suppresses duplicate events within a tick; opt-off spy asserts zero heap allocations across 1000 ticks
- **`OnboardingToastTests.cs`** — HTML contains toast markup only when `ProbeOnboardingSeen == false`
- **`ApiServerTraceContributeTests.cs`** — `/api/contribute-trace` handler packs file + POSTs to sibling URL

## 10. Non-goals

- No route-authoring UI inside POE2GPS. This is capture-only.
- No PII beyond what ExileCore2 exposes. Actually POE2GPS isn't an ExileCore2 plugin — no PII beyond what `Poe2Live` shipped surface exposes.
- No build detection. That's poe2open's problem.
- No automatic upload. The Contribute click is the entire "share" gesture.
- No hall-of-fame page for trace contributors. Deferred like SL #47.
- No trace-diff visualization. Users see the raw JSONL in a text editor.
- No cross-boot correlation for the Contribute button — users share one boot's worth per click. Multi-boot merging is a downstream concern.

## 11. Risks

- **PMS-14 stalls.** If LO doesn't run the Research probe, six of twelve events ship as stubs. Track A is still useful (six events cover the most-common route markers), but the pool is under-populated until PMS-14. Mitigation: onboarding copy doesn't over-promise.
- **File-size growth.** A 3-hour campaign session could produce 500 KB of JSONL. Per-boot rotation caps single files; Contribute uploads one boot at a time. Worker payload limit is 256 KB (per `MAX_BYTES` in worker.js) — enforce at Contribute time; gzip fallback for larger files → gist upload if still over.
- **Default-ON on 500 existing installs.** Users updating to v0.21+campaign-probe will find the probe on by default. Onboarding toast fires once and explains. Users can turn off in one click. This is a legitimate trust-contract shift; the CHANGELOG bullet must be explicit.
- **Public pool moderation.** Anyone can consume; anyone can pollute. The plausibility filter is the mitigation. If pollution spikes post-tag, add an approved-contributors allowlist to the merge script.
- **Cadence load.** 30 Hz tick with diff-observer scanning entity list adds ~200µs per tick when enabled. Zero-cost-when-off spy test verifies the gate. Enabled cost is acceptable given the read-only philosophy.

## 12. Task list preview

**Revised 2026-07-08 after upstream-offset extraction — single track, all 12 events live at ship:**

1. `PROBE-OFFSETS` — extend `Poe2Offsets.cs` with the 10 new offset groups from `scratchpad/campaign-probe-offsets.md` (character progression, quest flags/state, passive tree alloc vec + hop chain, Targetable component bytes, interaction components — Chest/Shrine/Transitionable/StateMachine, hover tracker). Extend `Poe2Live.cs` with accessor methods matching the shipped `PlayerInventories` chain pattern (read-only, no writes). Includes XP int64 → also closes PMS-6 (Long List #34 XP/hour Session HUD chip) as a free-rider.
2. `PROBE-RECORD` — `EventRecord.cs` (12 record structs + envelope + serializer, snake_case JSON keys byte-for-byte per §3).
3. `PROBE-ANON` — `AnonymizationHelpers.cs` (sha256-16 hex + UUID generator + settings-persistence).
4. `PROBE-WRITER` — `EventWriter.cs` (JSONL sink, per-boot rotation, async flush via `Channel<EventRecord>`, gzip-on-Contribute).
5. `PROBE-CORE` — `CampaignProbe.cs` (world-thread orchestrator, diff-observers for all 12 event types, UI-tree walk for NpcDialog + QuestReward panels using existing POE2GPS UI-walk primitives + shipped `hover.go` offsets, all events fire live).
6. `PROBE-SETTINGS` — `RadarSettings` additions (`EnableCampaignProbe = true`, `ProbeInstallId`, `ProbeOnboardingSeen`) + `SettingsMigrator` entry + auto-populate `ProbeInstallId` on first launch.
7. `PROBE-UI` — `DashboardHtml` Settings toggle + reset-session button + Contribute-trace button on Campaign panel + one-shot onboarding toast (Task 12's `showToast` helper reused).
8. `PROBE-CONTRIBUTE` — `ApiServer` `/api/contribute-trace` handler + Worker `/submit-trace` sibling route (fifth kind; middleware unchanged; issue label `community-pack` + `trace`).
9. `PROBE-TESTS` — full test set (serialization round-trip, hash determinism, per-boot rotation, opt-off spy asserting zero writes + zero allocs across 1000 ticks, onboarding-toast-shows-once, `/api/contribute-trace` handler round-trip) + README section + PMS-14 tracker entry finalized.

Ships parallel to v0.21 tag routine (no v0.21 surface changes; PMS-12 + PMS-13 unrelated).

---

Ready for LO review. On `go`, invoke writing-plans workflow for Track A (Track B is spec'd but blocked on PMS-14; plan lands post-Research).
