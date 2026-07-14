# Changelog

All notable changes to POE2GPS. This project is a strictly read-only, GGG-compliant PoE2 navigation overlay.
Versions are GitHub release tags (`vX.Y.Z`); the in-app update checker compares against the latest.

## [0.33.0] — 2026-07-13 "Ledger"

### Added — 📒 **Drop Timeline** *(persistent per-session record of your ground drops)*

- 📒 **Every non-white ground drop you observe gets logged** to `config/drop_timeline.json` while `EnableDropTimeline` is on. Name, rarity, zone, character, timestamp. Ring-buffered at 1000 entries — oldest drop off first once you saturate. In-memory dedup by entity id keeps the same drop from being recorded twice per session.
- 🗂 **New "Drops" dashboard tab** shows the log in reverse-chronological order — each drop is a rarity-colored card (Unique ochre, Rare yellow, Magic blue, Normal grey) with the item name, its zone, and a live "Xs ago / Xm ago / Xh ago" timestamp. Refreshes on tab open.
- 🔌 **`GET /api/drops` endpoint** exposes the same snapshot as JSON — feeds the dashboard and is available for OBS overlays or scripts. Empty envelope when the tracker isn't running.
- 🛡 **Compliance-first design** — same posture as the v0.30 Boss Wipe Log: local file only, no telemetry, no market pricing, no egress. Compliance-clean respec of the "historical price sparkline" idea from the v1.0 roadmap.
- ⚙️ **Off by default.** Flip `EnableDropTimeline` in `settings.json` to opt in for the persistent file — same convention as `EnableGearScorer` and `EnableItemFilterLiveCounters`.

### Added — 📸 **Session Recap PNG** *(one-click shareable 1920×1080 render on /obs)*

- 📸 **Floating "Save Session PNG" button** appears in the bottom-right of the `/obs` view (only there — never on `/map`). Click it and the browser renders a 1920×1080 canvas of your current session — character level, kills / rare kills / unique kills, deaths, maps per hour, XP per hour, zones entered, session length — laid out with a dark backdrop, a header stripe, and the github footer.
- 🖱 **One-click download** as `poe2gps-session-<timestamp>.png`. Drag into Discord, Twitter, Reddit. Substrate is the SSE session block already flowing — the recap improves automatically as more session stats land upstream.
- 🎯 **Zero server changes.** Pure client-side canvas render. No new endpoints, no new memory reads.

### Fixed — 🖼 **Per-filter counters + panel-open chip + filter sort/hide** *(v0.32 polish)*

- 🃏 **Per-filter live match counters** — each Item Filters card shows its own ground/equipped/inventory count instead of the same total smeared across every card. New summary strip on top of the tab shows the aggregate totals.
- 🟢 **Panel-open state chip** — the Item Filters tab now shows which panels are currently open (🟢 Character · 🟢 Inventory · ⚫ Stash), fed by `GET /api/panels` off the P1 panel resolvers. Real-time confirmation the resolvers work in production without needing another probe.
- 🔀 **Sort dropdown + hide-0-match toggle** on the Item Filters tab. Sort by name (default) / priority DESC / most matches now. Hide filters with zero current matches. Both persist to `localStorage`.

### Under the hood

- **Dashboard extracted from the C# raw-string embed to real asset files** — the ~3500-line inline HTML/CSS/JS in `DashboardHtml.cs` is now three embedded resources under `Web/Assets/dashboard/`: `dashboard.html` (80.6 KB), `dashboard.css` (29.3 KB), `dashboard.js` (140 KB). `DashboardHtml.cs` collapsed from 3541 LOC to a 40-LOC thin wrapper. Byte-for-byte identical output to pre-refactor (SHA256 pinned at every checkpoint during the 3-bead extraction). Unlocks JS lint, browser devtools debugging, editor syntax highlighting, and kills a whole class of encoding bugs that came from the C# raw-string embed. `AssemblePage` lazily loads the three assets on first `/` request and splices them via sentinel-comment replacement.
- New `Poe2Radar.Core.Session.DropTimeline` — thread-safe tracker mirroring the v0.30 `BossWipeLog` persistence pattern (load-on-construct + append-on-record + `Flush()`-on-dispose). Ring buffer via `LinkedList<T>` for O(1) eviction; in-memory `HashSet<uint>` for per-session dedup.
- New `/api/panels` endpoint providing character/inventory/stash open state (three `TryFind*Panel() != 0` checks per poll).
- Tick observation piggybacks the existing `_entities` walk right after `BuildItemLabels()` — no new memory reads, gates on `EnableDropTimeline`.
- 8 new xUnit facts covering `DropTimeline` (record, dedup, ring buffer cap, load, save, corrupt-file tolerance). Test suite: 739 → 747.
- `.gitattributes` gets `-text` rules on all three dashboard assets to preserve exact bytes across CI runners (prevents CRLF/LF normalization drift from breaking `Page` byte-parity).

### Deferred to v0.34+

- Highlight on **character equipment slots** — the panel resolver ships (v0.32); needs a one-shot slot fingerprint probe to pin the equipment-slot grid.
- Highlight on **stash grid tabs** — the stash panel resolver ships (v0.32); needs a stash-side inventory reader (`Poe2Live.ReadStashItems`) plus tab-switch detection.
- Specialty stash tabs (currency / fragment / essence / delirium / expedition) — each own drop.

---

## [0.32.0] — 2026-07-13 "Panorama"

### Added — 🖼 **Colored borders in-game on filter-matched inventory items**

- 🎯 **Your item filters now paint your bag.** Open the inventory panel and every cell whose item matches an enabled filter gets a border in that filter's color — the same colors your Item Filters dashboard cards show. Same match algorithm as ground drops: winner-takes-color when multiple filters hit (priority DESC, ties by list order), so your prioritized filters lead.
- 🧷 **Multi-cell items get one border spanning the whole slot rectangle.** A 2×2 body armour reads as one 2×2 highlight, not four tiny quadrants.
- ⚙️ **Gated behind two settings.** Flip both `EnableItemFilterLiveCounters` and `EnableInventoryHighlights` in `settings.json` (or the dashboard Settings → Advanced strip) to turn it on. Default off, same "opt-in for the memory read" posture as the God-Roll Detector.
- ⏱ **~1 Hz refresh cadence, ~1 s stale-close window.** Highlights refresh on the same 30-tick heartbeat the counters use — no per-frame memory cost. Closing the panel leaves the last painted cells up for at most a second before they clear. Documented tradeoff.

### Added — 📊 **Per-filter live match counters** *(each card shows its own count)*

- 🃏 **Each Item Filters card now displays its OWN ground / equipped / inventory count** instead of the same totals smeared across every card. An item that matches three filters bumps three counters — reading a card's number tells you "how many items would this filter highlight if it were the only one on."
- 📐 **New summary strip at the top of the tab** shows the aggregate totals across all enabled filters — one line, at-a-glance total: `🎯 total matches — ground: N · equipped: N · inventory: N`.
- 📦 **Equipped + inventory totals are live.** The v0.31 `/api/item-filters/matches` endpoint stubbed both at zero pending v0.32 — now they read from the same 1 Hz inventory snapshot that feeds the God-Roll Detector, so no new memory-read pressure when Gear Scorer is already on.
- 🕳 **Stash counter reserved but zero.** The payload envelope ships a `stash` field so the dashboard stays forward-compatible when a stash reader lands in v0.33+ — no reshape needed.

### Under the hood

- New panel-resolver framework at `Poe2Live.TryFindCharacterPanel` / `TryFindInventoryPanel` / `TryFindStashPanel`. Each uses an idx-hint fast path against the resolved UiRoot child, then falls back to a shape-fingerprint scan when the hint drifts (the same convention `Poe2Runeforge` uses for deep-panel walks). CharacterPanel vs StashPanel — which both anchor at the left edge — disambiguate on the presence of the stash-tab bottom bar's normalized-band fingerprint.
- New `Poe2Live.ComputeInventoryCellRect` pure math helper — takes a panel's unscaled screen origin + a cell's grid coordinates + grid dims + window size, returns a scaled screen-pixel rect. Directly unit-testable (no memory reads), used by the overlay renderer to place borders.
- New `Poe2Live.TryGetPanelUnscaledRect` + `Poe2Live.TryGetInventoryGridDims` — thin memory-read helpers over the already-validated UiElement + InventoryStruct offsets. Feed the resolver's panel handle into the math helper.
- New `Poe2Live.InventoryItem` slot fields: `SlotStartX/Y`, `SlotEndX/Y` (positional defaults appended — every existing 5-arg construction stays compiling).
- New `RenderContext.PanelHighlight` readonly record struct + `PanelHighlights` field — the render-thread contract for what to draw. Coords are unscaled UI base (2560×1600); the renderer scales at draw time so a mid-frame window resize can't skew the rects.
- New `RadarApp.BuildInventoryHighlights` internal static — pure aggregation from (inventory snapshot, filter engine, panel rect, grid dims) → highlight list. `RadarApp.CountPerFilterMatches` internal static — per-filter attribution for the dashboard card counts.
- New `Poe2Radar.Research --probe-panels` CLI walker for internal panel-fingerprint capture — interactively guides through Character/Inventory/Stash open/close cycles, diffs the UiRoot visibility bits, and prints normalized child fingerprints. Powers future v0.33+ probes for equipment slots + stash grids.
- 28 new xUnit tests: 10 panel resolver behavioral facts, 5 cell-rect math, 4 live-counter split, 3 per-filter attribution, 6 highlight builder. Test suite: 711 → 739.

### Deferred to v0.33+

- Highlight on **character equipment slots** — the panel resolver ships; needs a one-shot slot fingerprint probe to pin the equipment-slot grid inside the panel's content area.
- Highlight on **stash grid tabs** (regular / quad / jewel / map / relic) — the stash panel resolver ships; needs a stash-side inventory reader (`Poe2Live.ReadStashItems`) plus tab-switch detection before the highlight pipeline can attach.
- Specialty stash tabs (currency / fragment / essence / delirium / expedition) — each own drop.

---

## [0.31.1] — 2026-07-12 (Companion keypair rotation)

### Fixed — 🔐 **Rotated the Ed25519 supporter keypair before donations open**

- Rotated the Ed25519 keypair backing the Companion signed-supporter-code flow. The old public hex (`99392f...`) shipped with the v0.28 Companion drop was generated in-session while building the feature; before real Ko-fi donations start minting real codes, the keypair had to be rotated so the private half only ever existed in the Cloudflore Worker's encrypted secret store — never in git history.
- The new public key (`ac8da1...`) ships in `src/POE2Radar.Core/Support/supporter_public_key.txt`. The private half stays on the Worker only.
- Backwards-compat: donors who had already received v0.27-era hash-based codes still validate (the legacy path in `SupporterCodeValidator.IsSupporter` is untouched). Zero codes have been minted with the OLD private key, so this rotation invalidates nothing in the wild.
- 8 Ed25519 tests re-signed with the new keypair and all still pass; full suite 711/2/713 unchanged.

### Manual (LO — closes PMS-16 step 2)

- With this shipped, `wrangler secret put SIGNING_PRIVATE_KEY_HEX` on the deployed Cloudflare Worker will match the public key baked into POE2GPS v0.31.1+, so donor codes minted server-side will validate client-side on the first user launch after this release.

---

## [0.31.0] — 2026-07-12 "Prospector"

### Added — 🎯 **Item Filter engine** *(highlight items matching your desired affix combos)*

- 🎯 **New "Item Filters" dashboard tab** — card grid where each card is a filter with a name, border color, priority, enabled toggle, and a list of AND-linked requirements. Ships with 8 curated starter presets (ES Jeweler, Life Chest Baseline, Rare Amulet Baseline, Cast Speed Wand, Resist Ring, Faster Attacks Weapon, ES/Life Chest, Movement Speed Boots) disabled by default. Toggle any preset on, or click "+ New filter" to author your own.
- ✨ **Full stat-key DSL** — each requirement is a `statId + op (>=, <=, ==, between) + value` with optional scope (`prefix` / `suffix` / `implicit`) and optional `maxTier`. Match algorithm returns filters priority-sorted; the winning filter's color is drawn as the border in-game. Storage: `config/item_filters.json`. Full JSON round-trip via `/api/item-filters`.
- 💎 **Highlight on ground items** — dropped items whose affixes match an enabled filter now get a colored border on the ground label. Same shape as the existing unique-price highlight, per-filter color. When both apply, filter color wins over the legacy gold.
- 📊 **Live match counters** — each filter card shows how many items match on the ground right now. Equipped + inventory + stash counters land in v0.32.
- 🛡 **Restore starter presets** button — additively re-adds any preset id you've deleted (never removes your own filters).

### Fixed — 🗺 **/map view improvements**

- 🗺 **Fog reveal radius bumped 24 → 60 cells** (matches `AudioAlertRadiusCells` so both proximity systems agree). Community feedback: previous 24 was too tight in atlas + open zones. Also settings-configurable via `WebMapRevealRadiusCells` (Settings → Advanced) with range 20-200 — tune without a recompile.
- 🔍 **/map zoom with mousewheel + `+`/`-` keys.** Cursor-anchored zoom on the wheel (pixel under cursor stays put); center-anchored on keyboard. Range `0.5x`–`32x`. Persists across page reload via `localStorage`. HUD readout shows the current zoom level as `z<N.N>`.

### Under the hood

- New `ItemFilterEngine` in `POE2Radar.Core.Game` — load-on-construct + save-on-mutate + generation counter, mirrors the `DisplayRules` pattern. Storage `config/item_filters.json`. Match algorithm is thread-safe and allocation-friendly.
- New `default_item_filters.json` embedded resource — the shipped preset catalog. First-run copy materializes the seed to disk with `enabled: false`.
- New `/api/item-filters` GET/POST + `/api/item-filters/restore-presets` + `/api/item-filters/matches` endpoints.
- Extends `Poe2Live.ReadIdentityFromItem` to populate `EntityDot.ItemAffixes` on ground drops (respects the existing `_itemReadBudget` "read once per drop" contract). The affix data was already read for equipped items via the God-Roll Detector — this extension routes the same reader to ground drops.
- `ItemLabel.BorderColor` per-label field: `DrawItemLabels` honors it, replacing the hardcoded gold ColItemHi when set.
- 20 new xUnit tests (14 for the Match algorithm + 6 for storage/load/save round-trip + malformed tolerance + preset seed).

### Deferred to v0.32 "Panorama"

- Highlight on **character equipment slots** — data already flows (God-Roll Detector reads equipped items every 30 ticks); needs a one-shot live probe of the CharacterPanel UiRoot child index + slot fingerprints. Batched with v0.32's other panel walkers to share the probe session.
- Highlight on **player inventory panel** — server-side reads already ship; needs UI-panel walker + per-cell screen-rect projection.
- Highlight on **stash grid tabs** (regular / quad / jewel / map / relic) — InventoryStruct layout reuses; needs stash-panel walker + tab-switch detection.
- Specialty stash tabs (currency / fragment / essence / delirium / expedition) — each own bead in v0.33+.

---

## [0.30.0] — 2026-07-10 "Instinct"

### Added — 🪦 **Per-character boss wipe log** *(persistent · cross-session · discoverable)*

- 🪦 **Every death in a matched boss zone is logged** against your character name in a new persistent file at `config/boss_wipe_log.json` (schema: `{ characters: { charName: { bosses: { bossKey: count } } } }`). The next time you walk into that boss, the cheat-sheet panel title bar gets a **"🪦 Nx before"** tag so future-you sees what past-you learned.
- 📊 **New dashboard "Your wipe log" card** on the Bosses tab. Shows your current character, total wipes across all bosses, per-boss count sorted by "what's killing me most", plus a list of other characters on record. Feeds off a new `/api/wipe-log` endpoint that maps `bossKey → label` via the shipped `BossEncounterCatalog`.
- ⚙️ **Opt-out** with `TrackBossWipes = false` in `settings.json`. No data ever exfiltrates; the log file lives next to your other configs and can be nuked at any time.
- 🧠 **Only tracks boss zones** (matched cheat-sheet entries) — regular map deaths don't pollute the log. This makes the "what am I struggling with" surface actually useful long-term instead of a noise heap.

### Added — 💥 **Boss panel damage-type chip strip** *(finally, actually colored)*

- ☠ The boss cheat-sheet panel's damage-type row is no longer plain text — it's now a strip of **colored chips** matching the dashboard's boss card: `phys` cream · `fire` orange · `cold` blue · `ltng` yellow · `chaos` purple. Skips elements < 5% share. Reads at-a-glance in-fight instead of parsing a text list.

### Added — ⭐ **Waystone click-to-flag** *(personal red-flag list, remembered forever)*

- ◈ Click any mod row in the waystone panel to **toggle a ★ personal red-flag** on that mod name. Flagged mods get a ★ prefix regardless of the built-in Safe/Notable/Deadly verdict — "I have died to this mod combination before, don't miss it again." Persisted in `settings.WaystoneRedFlags` and immediately visible on the next parse.
- Never gates functional behavior — the parser still reports the same tiers to the dashboard. Cosmetic-only visual nudge, tailored to YOUR pain points instead of the shipped catalog's.

### Added — 🧪 **Panel state-machine tests** *(safety net for the panel logic)*

- 10 new xUnit tests in `WipeMemoryTests.cs` locking the wipe-counter contract: null/empty guards, increment semantics, snapshot independence, ctor tolerance, ClearZone/ClearAll behavior. Filed through the beads pipeline and executed by the `openrouter/qwen3-coder` worker — landed clean, `10/10 passed`, no regression.

### Under the hood

- New `WipeMemory` class — pure per-character counter, unit-tested, reused inside `BossWipeLog`.
- New `BossWipeLog` class — thread-safe, load-on-construct + save-on-mutate, tolerant of a missing / corrupt log file (starts empty, never crashes).
- `WorldSnapshot` gained a `PlayerName` field (from `_live.PlayerName(localPlayer)` which self-caches per localPlayer address) so the render thread has a stable identity key for the wipe log.
- New `/api/wipe-log` endpoint served by `ApiServer`, wired via a `wipeLogProvider` Func passed at ctor. Serializes `{ character, wipes, total, allCharacters }` as gzipped JSON like the sibling endpoints.

### Deferred to v0.31

- Dashboard "clear this boss" / "reset character" buttons for the wipe log (data model + endpoint already support it — just needs UI wire-in).
- Waystone red-flag: bulk-import a shipped "meta danger list" of community-flagged mods so first-time users get a sensible starting flag set.
- Damage-type icons via the atlas icon cache (chip strip is a strong interim; PNG glyphs could come later).

---

## [0.29.0] — 2026-07-10 "Panels"

### Added — 📋 **Panels** *(two new in-game overlay panels that pop when you need them and disappear when you don't · closable · collapsable · auto-dismissed on next zone entry)*

- ☠ **Boss cheat-sheet panel** *(top-left, auto-opens on boss zone entry)*
  - When the player enters a zone whose code matches a [`BossEncounterCatalog`](src/POE2Radar.Core/Game/BossEncounterCatalog.cs) entry (the same catalog that already ships in v0.25 Chorus + backs the Bosses dashboard tab), a translucent overlay panel pops up at top-left showing: **tier · category**, **damage-type mix** (phys / fire / cold / lightning / chaos shares, ≥ 5% only), **one-shots to dodge**, **phase cues** (HP threshold → note), **over-cap resist targets**, and **flask notes**. All from the catalog — no new data authoring; every existing pinnacle entry already reads.
  - **Closable (✕)** — dismiss the panel entirely until the next zone entry.
  - **Collapsable (▶/▼ caret)** — collapse to just the title bar to keep it in view but out of the way.
  - **Auto-dismissed** on next zone change — walk into a new zone and the panel is gone (or replaced, if the new zone is ALSO a boss arena).
- ◈ **Waystone risk panel** *(top-right, opens on `Ctrl+Alt+W` hotkey)*
  - Press `Ctrl+Alt+W` with a waystone copied to clipboard → the overlay parses it via [`WaystoneModRisk`](src/POE2Radar.Core/Game/WaystoneModRisk.cs) (the same parser the Waystone dashboard tab uses) and pops a panel showing: 🚨 **SKIP** banner when total risk ≥ 60, **rarity · tier · score**, **per-tier colored mod rows** (Deadly red, Notable orange, Safe green, LethalCombo dark red), and **triggered combos** with their bonus scores.
  - **Same close (✕) / collapse (▶/▼) / auto-dismiss** as the boss panel — dismiss OR walk into the next zone, whichever comes first.
  - Clipboard read is retry-safe (a Ko-fi tab / Discord / another app can briefly hold the clipboard without breaking the hotkey).

### Changed — 🌍 **Atlas display sites now speak your language**

- The `Language` setting shipped in v0.26 Reach now actually reaches the atlas display surfaces: 5 call sites in `RadarApp.cs` (dashboard `allMaps` filter list, dashboard `nodeList` per-node card, F10 atlas-tile inspector console output, and the **in-game atlas overlay label** — the highest-visibility one, drawn every frame on tracked atlas tiles) route through a new `LocalizedMapName(mapCode, fallback)` helper that reads `MapMeta.LocalizedName(_settings.Language)` and falls back to English if the key is missing or the setting is empty. Backwards-compatible: existing English users see identical strings.
- **Not** changed: the seed sites at `RadarApp.cs:3412-3413` (byCode dict) + `:3537-3538` (Citadel filter) + `:3636` (group color lookup) intentionally stay English — they're the match KEYS the rule system uses to identify tracked maps. Localizing them would break every existing user's atlas rules.

### Fixed — 💡 **Rules tab picker empty-state hint**

- When you open the Add-from-game-data picker in a zone that has none of the entities/tiles you're looking for (e.g. sitting in town when you want to add a Breach rule), the empty-state message now explains the workaround: *"Enter a Breach zone first, or close this and click Add blank rule — the match field now suggests Breach, Ritual, Expedition, Boss… as you type."* Closes gap B from the v0.28.1 audit.

### Under the hood

- New `Overlay/Native/ClipboardText.cs` — a tiny Win32 P/Invoke helper (OpenClipboard → GetClipboardData(CF_UNICODETEXT) → GlobalLock → PtrToStringUni) with a 4-attempt retry loop that survives another app briefly holding the clipboard. Read-only; the overlay never WRITES to the clipboard.
- New `Keybinds.WaystoneRisk` VK code (default `0x57` = W) added to `KeybindsSettings` alongside the existing rebindable keys. Persisted like all other keybinds.
- Two new panels share the existing overlay click-through / hit-rect infrastructure — no new input plumbing. Each panel's ✕ and caret each register their own `_legendRowRects` entry with distinct actions (`boss-close`, `boss-collapse`, `waystone-close`, `waystone-collapse`) that route through `OnOverlayClick`.
- Zone-change edge wired inside the existing `WorldTick` areaInstance-diff block — the same edge that clears the preload dedup sets and resets per-zone counters. One place, all reset.

### Deferred to v0.30

- Boss panel content-icons (currently text-only; would benefit from the Direct2D atlas-icon rendering path).
- Waystone panel: click a Deadly mod row to seed a Hidden-cull rule for that mod key.
- Panel position customization (currently boss = top-left, waystone = top-right; hardcoded).

---

## [0.28.1] — 2026-07-10 (rule suggestions)

### Fixed — 💡 **Rules tab match-field autocomplete + friendlier hint**

- 💡 The Rules tab match input now suggests common mechanic / entity names (**Breach**, **Ritual**, **Expedition**, **Essence**, **Strongbox**, **Shrine**, **Boss**, **Chest**, **NPC**, and more) as you type — pulled from the same curated `labels.json` vocabulary the Director + Entity Atlas tabs already use. Community-reported: a supporter wanted to add Breach to their rules and had no way to discover the term without reading the source. The label refactor in v0.24 renamed the seeded default rule to "Breach (Rift)" for clarity, but the underlying match term (`Breach`) is unchanged — this hotfix just makes that discoverable at the point of use.
- 💡 Placeholder updated: `match: metadata terms, comma-separated (blank = any) — try Breach, Expedition, Ritual, Boss…` so first-time visitors see valid examples inline.
- Nothing else changed. No new deps, no schema change, no migration, no backend touched — just three lines of dashboard HTML/JS.

---

## [0.28.0] — 2026-07-10 "Companion"

### Added — 🌐 **Companion** *(the eloquent supporter flow: Ed25519 signed codes end-to-end · Ko-fi → email → Discord role · no per-donor releases · no shipped hash list)*

- 🔐 **Ed25519 signed supporter codes.** New `SupporterSignedCode` verifier (in `Core/Support/`) uses BouncyCastle's Ed25519 primitives against a shipped `supporter_public_key.txt` embedded resource. Codes are formatted `poe2gps.<base64-payload>.<base64-signature>` — the payload carries the donor's email, tier, and issued timestamp; the signature is Ed25519 over the payload bytes. The private key never touches POE2GPS — it lives only on the Cloudflare Worker. Anyone extracting the exe gets the public key (useless for minting) instead of a hash list. Backwards-compatible: the v0.27.1 hash-based codes still validate through the same `IsSupporter` gate, so nobody's code stops working.
- 🌐 **Cloudflare Worker** at `cloudflare-worker/supporters-worker/` that receives Ko-fi webhooks, mints signed codes with the private key, emails the code to the donor (via Resend by default; drop in any provider), assigns the `☕ Supporter` Discord role via the bot API when the donor pastes their Discord handle in the Ko-fi donation message, and posts a `🎉 New supporter!` announcement to a Discord channel webhook (optional). Full deploy guide in `cloudflare-worker/supporters-worker/README.md`.
- 🤖 **Discord auto-role**. Ko-fi → Worker → Discord API. Donors add `discord: theirhandle` to the Ko-fi donation message and get the `☕ Supporter` role automatically (bot needs `Manage Roles` + role position above Supporter). Silent no-op when the handle is missing — donation still processes, code still emails.

### Changed

- `SupporterCodeValidator.IsSupporter` tries the Ed25519 signed-code path first, then falls back to the legacy hash-list check for v0.27-era codes. Full end-to-end backwards compatibility.
- Added `BouncyCastle.Cryptography` (~4 MB pure managed, no native deps) as the only new NuGet dep in `POE2Radar.Core` since the atlas port. Keeps the project's read-only compliance envelope intact.

### Manual (LO — see PMS-16)

- Regenerate the Ed25519 keypair before production use (the current shipped keypair was generated in-session with the sample code live in a test file — fine for dev, not for prod). Deploy the Worker with the new private hex secret; update the public hex in the app + regenerate the sample code test. Details in `cloudflare-worker/supporters-worker/README.md`.

### Deferred to v0.29

- Language wire-in for atlas display sites.
- Boss cheat-sheet overlay panel.
- Waystone Ctrl+Alt+W hotkey.
- Long List #39 Full-page browser views.
- Supporter-only preset packs.
- Roadmap voting card.

---

## [0.27.1] — 2026-07-10 (support automation)

### Added

- 🔧 **Maintainer helper for the supporter code flow.** The v0.27.0 supporter-code system required LO to compute SHA-256 hashes in a shell, edit C# source, and rebuild for every new Ko-fi donor. This drop moves the hash list out of C# into an embedded `supporter_hashes.json` and adds a dashboard admin section (visible via `?admin=1` on the dashboard URL) that does the whole flow in one place: type a raw code (or 🎲 generate a random one), the SHA-256 auto-computes live via WebCrypto with a Copy button, and paste-ready snippets for `supporter_hashes.json` + `supporters.json` + a Ko-fi DM template render as you type. LO's flow: type/generate, click 3 copy buttons, paste, commit, release, send the DM.
- ☕ **Real seed code shipped.** The v0.27.0 hash list was placeholder — nothing validated out-of-the-box. This drop ships a real working code (`POE2GPS-FIRST-COFFEE-2026`) so the cosmetic-unlock feature is discoverable immediately. Case + whitespace tolerant on paste.

### Changed

- **`SupporterCodeValidator.Hashes` migrated to `supporter_hashes.json`.** The C# `HashSet` is gone; the loader (`Lazy<HashSet<string>>`) reads the embedded JSON at first use. Adding a new code = edit ONE JSON file. Malformed / missing JSON fails closed (no code validates) rather than crashing.

---

## [0.27.0] — 2026-07-10 "Support" 🤝

### Added — 🤝 **Support** *(the community-first release · every supporter gets a place on the roll · cosmetic perks for Ko-fi backers · today's ES-offset patch baked in)*

- 🩹 **ES-offset patch baked in.** GGG shifted the EnergyShield offset again today (0x264 → 0x24C). The auto-heal fixed it correctly for everyone the moment they launched, but nobody should have to pay that startup cost on every launch. Baked the new offset into the shipped default so v0.27+ launches clean; the auto-heal stays as the belt-and-suspenders backstop for any *future* drift.
- 🤝 **Supporters card v2 — total count + latest supporter + rotating pitch.** The Supporters card at the top of Settings now shows the live community-backer total, the latest supporter's name in gold, and rotates through five community pitch quotes so the message stays fresh across visits. Pill roll below still shows every backer with a tier color and a hover-title for their role.
- 📄 **SUPPORTERS.md hall of fame.** New top-level [`SUPPORTERS.md`](SUPPORTERS.md) is a browsable markdown table sorted by tier (🥇 Gold / 🥈 Silver / 🥉 Bronze / 💛 Community) — auto-generated from `supporters.json` so every release ships a fresh copy. Also linked from the README so it's discoverable without opening the app.
- ☕ **Ko-fi supporter code + cosmetic dashboard palettes.** Ko-fi backers now get a code (LO ships codes via Ko-fi email / Discord DM after donations). Paste the code into ⚙️ Settings → **Supporter code**, and two cosmetic palettes unlock: **Kalguuran Gold** (warm gold on deep amber, callback to the Kalguuran act aesthetic) and **Wraeclast Terminal** (green-phosphor CRT). Also unlocks an optional **☕ Supporter chip** on the Session HUD — off by default; toggle in Settings. All cosmetic — the tool's functional surface stays identical for everyone forever. `SupporterCodeValidator` in `Core/Support/` uses SHA-256 on shipped hashes; local-only, never phones home, honor-system gate.
- 📝 **README Ko-fi pitch rewritten.** The Ko-fi section now leads with the free-forever promise, then makes the actual pitch (what a coffee funds), then lists the three community perks: Supporters-roll placement, cosmetic unlock code, and Discord `☕ Supporter` role. Sets the tone that this is a community-first drop, not a paywall migration.

### Deferred to v0.28 "Companion"

Everything from v0.26's deferred list plus this drop's scope-cuts:

- **Language wire-in** for atlas display sites (the setting reads but no site consumes it yet).
- **Boss cheat-sheet overlay panel** (dashboard tab is the browsable surface; overlay panel is reactive-on-arena-entry).
- **Waystone Ctrl+Alt+W hotkey** (grab clipboard + open the tab).
- **Long List #39** Full-page browser views (Rules / Landmarks / Nameplates).
- **Supporter-only preset packs** (3-4 curated `.poe2preset` files).
- **Roadmap voting card** (supporters get 3 votes / non-supporters 1).
- **Ko-fi webhook automation** (email code delivery).

---

## [0.26.0] — 2026-07-10 "Reach"

### Added — 🌏 **Reach** *(boss cheat sheets · waystone mod-risk warnings · localized atlas names · dashboard groupings · a supporters roll · issue templates get a lane for post-patch drift)*

- 📚 **Boss encounter cheat sheets.** New `Bosses` tab in the dashboard reads a shipped `BossEncounterCatalog` (5 pinnacle entries seeded — Arbiter of Ash, Xesht, Kosis, The Maven, The Bodach — hand-authored, paraphrased from public wiki summaries). Each card shows the boss's damage-type mix (color-coded pills), the top one-shots to dodge, over-cap thresholds by element, flask notes, and phase cues. `BossEncounterCatalog.ByBossKey` / `ByZoneCode(MapUberBoss_*)` / `ByMetadata(...)` surfaces are already wired for an overlay panel in a follow-up drop.
- ⚠️ **Waystone mod-risk parser.** New `Waystone` tab: paste a Ctrl+C'd waystone item text and get a tiered mod list (Safe / Notable / Deadly), triggered danger combos (reflect+crit, no-leech+no-regen, etc), a total risk score, and a red **SKIP RECOMMENDED** banner when the score ≥ 60. Rules and combo table live in embedded JSON (`poe2_waystone_mod_risk.json`) so future mod additions don't need a code release. Server-side `/api/waystone/parse` is loopback-gated; the tab renders results in-place.
- 🌐 **Localized atlas map names.** `AtlasMapData.MapMeta` now exposes `Translates` (10 languages: english, french, german, japanese, korean, portuguese, russian, spanish, thai, traditional chinese) and a `LocalizedName(language)` helper. `RadarSettings.Language` defaults to Windows system locale on first launch (via `CultureInfo.CurrentCulture.TwoLetterISOLanguageName` mapped to the shipped keys); English fallback for everything else. Wiring the language into the display paths (Poe2Atlas emission + dashboard picker) is scheduled for a follow-up so the setting has an effect out of the box.
- 🗂️ **Settings tab section-header dividers.** The 22-card Settings tab now shows section dividers between the natural card groups: `HUD panels`, `Overlay rendering`, `Advanced`, `Integrations`. Full-width grid rows with a Cinzel-styled label — cards flow into the next section on the same panel-grid. Zero JS refactor: `wireSettings()` uses document-wide `[data-set]` selectors that survive the DOM reshape.
- ☕ **Supporters card on the dashboard.** New `Supporters` card at the top of Settings shows a name-pill roll seeded with the existing contributors (LO, torx, Kaonashi, Diamondsr, Sidefx, Verahsa). Tier keys (`gold` / `silver` / `bronze` / `community`) drive the pill color; each pill's `title` attribute shows the contributor's role on hover. Card also carries the Ko-fi call-out. Backers get added by editing the embedded `supporters.json` and shipping a release — no CI schema, no server-side auth.
- 🙏 **Two more names in the Special thanks section of the README** — `Sidefx` and `Verahsa` for continued community feedback and testing help.

### Deferred to v0.27 "Companion"
- Localization: wire the `RadarSettings.Language` into the atlas display path (currently the setting reads but no display site consumes it yet).
- Overlay boss cheat-sheet panel that surfaces the current-zone entry on arena entry (dashboard tab is the browsable surface).
- Waystone card global hotkey (Ctrl+Alt+W) that grabs clipboard + opens the tab.
- Long List #39 Full-page browser views (Rules / Landmarks / Nameplates).

---

## [0.25.1] — 2026-07-10 (hotfix)

### Fixed

- 🚨 **`OverlayRenderer.DrawMap` crash on entity/landmark list race.** After a game patch shifted the ES offset (0x264→0x24C, auto-heal fired successfully) some users hit a fatal `System.InvalidOperationException: Collection was modified` inside `OverlayRenderer.DrawMap`'s entity/landmark loops. Defensive fix wraps both `foreach (var e in ctx.Entities)` and `foreach (var lm in ctx.Landmarks)` in index-based iteration + try/catch — if the world thread re-slices the list mid-render the current frame's remaining dots/landmarks are dropped and the overlay recovers on the next present. No feature change, no data loss — just no more crash.

---

## [0.25.0] — 2026-07-10 "Chorus"

### Added — 🎼 **Chorus** *(three new Zone Summary chips light up the corner HUD)*

- 📊 **Zone Summary: kills-this-zone chip.** Always visible next to the `Monsters` row. Increments alongside session kills but resets to zero on every zone entry, so you can tell at a glance how much of the current map you've actually cleared. `KillTracker` grew a parallel per-zone counter that clears in `ClearZone()` and increments in lockstep with the session counter — session totals stay untouched by zone resets. Locked by three new `KillTrackerTests` cases.
- 🌀 **Zone Summary: nearest-mechanic chip.** When any league mechanic (Runestone / Ritual Altar / Breach / Strongbox / Essence Monolith / Shrine) is loaded in the current zone, the panel shows a `Nearest  <kind>  <distance>` row. Distance is grid-units from the player, computed in the same entity walk that produces the mechanic counts. Tier-ranked so a Ritual/Breach beats an Expedition at the same distance.
- ⭐ **Zone Summary: boss-arena flag.** `★ Boss Arena` lights up when the current zone contains any Unique-rarity entity whose metadata carries `BossArena`. Runs off the same entity walk — zero new memory reads, zero new tick cost.

### Deferred to v0.26 "Reach"
- Settings tab five-group section headers (Short List #7) — the DOM restructure needs its own reviewed drop.
- Waystone/map mod-risk warning card (Long List #41).
- Boss encounter cheat sheet (Long List #42).
- Localization pipeline (Long List #38) + full-page browser views (Long List #39).

---

## [0.24.0] — 2026-07-10 "Groove"

### Added — 🎧 **Groove** *(dashboard shortcuts land · Discord Rich Presence gets 5 new tokens · confusing radar labels rewritten · issue templates get a patch-drift lane and the healer log gets its due)*

- ⌨️ **Dashboard keyboard shortcuts + help modal.** Press <kbd>/</kbd> to focus the search box on the current tab, <kbd>1</kbd>–<kbd>7</kbd> to jump between tabs (Rules / Landmarks / Atlas / Settings / Director / Entity Atlas / Gear), <kbd>?</kbd> to toggle a shortcut cheat sheet, <kbd>Esc</kbd> to close any open modal and cancel keybind capture. Shortcuts sit out while you're typing in a text input. Also lays the plumbing for a central save-toast (`flashSaved()`) that new callsites can adopt without touching the 10 per-card `savedMsg*` spans in flight today.
- 🎮 **Discord Rich Presence: 5 new tokens.** Templates can now use `{hp}`, `{mana}`, `{es}`, `{deaths}`, and `{boss}` in addition to the existing `{area}`, `{level}`, `{zones}`, `{mapshr}`, `{kills}`, `{xpeff}`. `{boss}` lights up as "in boss arena" when the current zone contains any Unique-rarity entity — cheap O(entities) scan on the 15 s presence cadence, no new memory reads. Dashboard preview mirrors the same token set.
- 🏷️ **Radar label pass.** "Expedition" now renders as "Runestone (League Event)" — one user report we sat on for too long. Also: "Ritual" → "Ritual Altar (League Event)", "Breach" → "Breach (Rift)", "Essence" → "Essence Monolith", "Abyss Crack" → "Abyss Pit (League Event)", "Quest Object" → "Quest Item", `EinharQuestMarker` → "Einhar (Bestiary NPC)". All 3 shipped presets (boss_hunter, high_contrast, minimal) get the same rewrites. No behavior change — the metadata match patterns stay identical, only the display label softens.
- 🐛 **Patch-drift issue template + CONTRIBUTING.md expansion.** New `.github/ISSUE_TEMPLATE/patch-drift.yml` collects the fields the maintainer actually needs after a game patch shifts memory offsets: your POE2GPS version, the PoE2 patch, the subsystem that broke, the healer-log lines (POE2GPS auto-heals vitals offsets on startup and prints `auto-relocated 0x{old}→0x{new}` — that line goes in the report). CONTRIBUTING.md now covers the four data streams (atlas / buffs / preload / **trace**), points at the roadmap for feature requests, and links Discord + Discussions from the top. Feature-request template asks users to check the roadmap first.
- 🔗 **Stale Discord URL fixed.** `config.yml` was pointing at `discord.gg/poe2gps` (never existed); now points at the canonical `discord.gg/32qdzWRja3` matching README.

### Deferred to v0.25 "Chorus"
- Settings tab five-group section headers (roadmap Short List #7) — the DOM restructure earned its own reviewed drop.

---

## [0.23.0] — 2026-07-10 "Signal"

### Added — 📡 **Signal** *(data flowing where it should — probe traces reaching the community pool · alerts audibly signaling · terrain visible for the first time · preload panel actually manageable)*

- 📈 **Campaign Probe (opt-out, on by default).** Since v0.22 POE2GPS has quietly gathered anonymized zone-traversal, level-up, boss-encounter, checkpoint-touch, waypoint-unlock, area-transition, and passive-allocation events into a local JSONL file at `%APPDATA%\poe2gps\campaign_traces\`. That data now has a home: every Contribute click (atlas / buffs / preload) auto-piggybacks a trace upload to the community pool, so users who already contribute never need to think about it. Toggle in ⚙️ Settings → **Enable Campaign Probe** — flip off to disable both collection AND upload, no restart needed. Install-ID resets on request from the dashboard so you can start clean any time. UI-tree observers (dialogue, quest-reward) remain silent pending an in-game verification pass; every event that fires today is verified live.
- 🗺️ **`/map` renders terrain again — for real this time.** The walkable-terrain layer has been silently missing since v0.20.0. Path polylines rendered on a dark background, so the symptom read as "map ok, colors dark" rather than "no terrain layer." Root cause was a wire-format mismatch between `/api/map` (JSON number `areaHash`) and `/stream` SSE (hex-string `area`) — the client compared them with strict `!==` and always rejected the terrain payload. One-line client-side coercion fixes it, plus a regression test that locks the both-sides contract so a future refactor can't silently drift back.
- 🔊 **Alert volume slider works now.** Dragging the volume slider posted its value as a JSON string; the server-side `TryInt` gate rejected strings; the setting was silently dropped and audio cues kept the boot-time default. One-line fix in `wireSettings()` coerces `type="range"` values to Number before POST.
- 🗂️ **Preload panel: collapse toggle + hide-on-spawn.** Click the caret next to `PRELOAD` in the panel title to fold everything but the title; click again to reopen. Preload rows for bosses and unique monsters now hide from the panel automatically once the entity appears in the live entity list — the panel stays clean as encounters resolve. Shrines / Chests / Rituals are tile-scoped and stay visible until zone change (no spawn-detection binding for those categories).
- 📤 **Trace uploads piggyback on every Contribute click.** The `#tpContribute` button in the Zone Plan card stays for manual sends but now shows an `auto-fires with atlas contributions` subtitle. Piggyback POSTs are fire-and-forget — a trace failure at the Worker (or a `enableCampaignProbe=false` toggle) never blocks the primary Contribute checkmark.

### Fixed

- **`/map` black-background regression** (see the `/map` bullet above — technically a v0.20.0 latent bug that no one noticed because the polylines still rendered).
- **Alert volume slider no-op** (see above — v0.22.x-and-prior behaviour was a silent write-drop).

---

## [0.22.0] — 2026-07-09 "Threshold"

### Added — 🚪 **Threshold** *(waygates render as waygates · XP/hour lands on the Session HUD · monolith panel collapses when you're done reading it · atlas content icons snap back to true)*

- 📈 **XP/hour on the Session HUD.** *(opt-in, off by default)* A new **XP/hr** row lives inside the existing Session HUD panel — no new panel, no new hotkey. Enable in ⚙️ Settings → Session HUD → **Show XP rate**. Rolling window is user-tunable from **1 to 60 minutes** (default 5), mirrored on `/api/settings` as `sessionHudShowXpRate` + `sessionHudXpWindowMinutes`. The ring survives zone crossings (it's a grind metric, not a zone metric); town frames don't append so hideout time doesn't drag the rate — reuses the existing **Exclude Towns From Pace** toggle. **Ctrl+Alt+R** resets it alongside the rest of the HUD. While the window is still filling (roughly the first 5 minutes) the row prints a **session-average fallback rate** so you see a live number immediately; once the ring is full it switches to the true windowed rate. When there are enough samples, the row also prints a `(Nm to L##)` **time-to-next-level** estimate off the built-in level curve. **Zero-cost when off:** with the row disabled, the fallback character-XP read is skipped entirely — a spy test locks the guarantee for 1000 disabled ticks. Closes **PMS-6** (Long List #34, XP/hour Session HUD chip).
- 🚪 **Waygates render as tracked landmarks.** Built-in Tile display rule ships for the end-game `WaygateDevice` entity — Navigable, Eye-shape marker, distinct cyan — so waygates surface on the radar the moment they enter range. Idempotent one-shot migration (`built_in_tile_rules_v1`) folded into the `AppliedMigrations` list; upgrading from v0.21 seeds the rule once, additive-only, no state loss. An explicit **exactly-one-marker** test guards the row against a future atlas-landmark port silently double-stamping the same entity.
- 🩹 **Atlas content-icon draw fix.** Content-icons stamped on fogged atlas nodes (Breach / Boss / Essence / Expedition / …) were mis-rendering their destination rect at high zoom levels — one axis of the square was pulling the wrong dimension. Pattern-matched port straightens the rect so icons stay pixel-aligned to their node at every zoom, and the math is extracted behind a pure helper so a regression trips at unit-test time.
- 🗂️ **Click-to-collapse nearby-monolith reward panel.** New caret on the panel's title row toggles a persisted collapsed state — the reward rows hide, the title stays. `MonolithsTop` pre-sort/cap-to-6 semantics preserved: POE2GPS's monolith prioritization is untouched by the collapse toggle.

### Fixed

- Nothing user-visible beyond the atlas content-icon rect above.

### Compliance

- 🛡️ **100% read-only.** Zero new memory writes. Zero new offset writes. Zero new input paths. Every new setting respects zero-cost-when-off. v0.20 wire format additive-only — no SSE key removals, no rename.

## [Unreleased] — v0.21 "Guided Campaign"

### Special thanks
Enormous thanks to **syrairc** for green-lighting [ExileCampaigns2](https://github.com/syrairc/ExileCampaigns2)'s integration into POE2GPS. v0.21's campaign step guide is a direct port of upstream route data + advance logic. Upstream: <https://github.com/syrairc/ExileCampaigns2> · license: `TODO(syrairc-license)` · commit: `TODO(syrairc-hash)`.

### PMS-13 deploy runbook (maintainer)

The v0.21 Cloudflare Worker rewrite splits `/submit` into three sibling routes
(`/submit-atlas`, `/submit-buffs`, `/submit-preload`) with a shared NFKD+leet profanity
filter and a KV-backed 5/60s rate limit. The KV binding requires a namespace ID that only
the maintainer's Cloudflare account can mint, so deploy is a manual step:

```
cd cloudflare-worker
wrangler kv:namespace create RATE_KV        # capture the returned id
# paste the id into wrangler.toml, replacing the placeholder sentinel
wrangler deploy
wrangler secret put GITHUB_TOKEN            # if not already set
bash ../resources/poe2-data/smoke-worker.sh https://poe2gps-contribute.<you>.workers.dev
```

Only after `SMOKE PASS` prints may the `CF-DASH-BUTTONS` PR open — ordering gate from
the v0.21 spec §12 (stale desktop clients hitting new routes before deploy see a clean 404
rather than a schema-mismatch 400).

### Changed — 🧭 **Guided Campaign** *(ExileCampaigns2 route + advance engine on-board · community pipeline hardened for real contributors)*

- 🧭 **Campaign step guide from syrairc's ExileCampaigns2.** The full ExileCampaigns2 route data (602 KB) + advance-engine logic ported to POE2GPS under `Campaign/Guide/`. Ships as a **Parallel Rail** — the new step guide runs alongside (not replacing) the existing Campaign GPS + Objective Director, hooked at `RadarApp.CampaignReconcile`. Enable via ⚙️ Settings → Campaign GPS; the panel renders on the **Director tab** of the Dashboard.
- 📡 **CampaignGuide additive SSE key.** New immutable `CampaignStepInstruction` record struct published alongside `CampaignGps` on `/stream`. v0.20.x clients keep working — additive-only wire format is non-negotiable, locked by golden-DTO snapshot tests.
- 🎯 **Graceful area-boundary forward-snap.** Six advance signals are stubbed until v0.22's quest-flag reader (`QuestFlagSatisfied`, `WaypointPulsed`, `SatisfiedFlagCount`, `TalkProgress`, `InteractProgress`, quest-item `LootSatisfied`). Four live signals ship today: area, proximity, kill, player-inventory loot. Steps whose only advance signal is stubbed stall until you cross into the next zone — the cursor forward-snaps past them at the area boundary. Persistent Campaign-panel badge preempts bug reports.
- 🖥️ **Campaign panel on Dashboard.** New step text row + graceful-degradation badge + persistent syrairc attribution with clickable link to ExileCampaigns2. Zero-cost-when-off: the panel elements ship with `hidden` in the static markup and stay that way until `CampaignGuide` populates.

**Community pipeline hardening:**

- 🚀 **Three sibling Worker routes** — `POST /submit-atlas` (existing shape, v0.20.x backward-compat), `POST /submit-buffs` (buff metadata + tier), `POST /submit-preload` (metadata paths only, rejects bare `.dds`/`.ao`). Shared middleware: NFKD-normalized leet-fold profanity filter (kills the leet-substitution bypasses of the slur list), simple KV counter rate limit (5 requests / 60s per `CF-Connecting-IP`), gh dispatch. Stale desktop clients hitting the old `/submit` route see a clean 404 instead of a schema-mismatch 400.
- ➕ **Buff + preload Contribute buttons** on their respective ⚙️ Settings cards — the buttons appear once you enable the **Buff icons** and **Preload Alert** cards. New `/api/contribute-buffs` + `/api/contribute-preload` handlers pack observed data from the existing `/api/buffs` and `/api/preload` sources and forward to the Worker. `merge_community.py` gains buffs + preload fold branches targeting `poe2_notable_buffs_community.json` and a preload-community sidecar.
- 🔕 **Silent-fallback fix (SL #15).** Missing Contribute URL used to silently open a generic GitHub issue form — contributors thought they submitted but hadn't. Split into two distinct sentinels (`settingsFetchFailed` vs `contributeUrlEmpty`) with actionable toasts. Empty URL surfaces a "Restore default URL" action; settings-fetch failure surfaces retry copy. Applied across all three Contribute buttons.
- 📝 **Contribution surface: bug-report + feature-request + config issue templates + root `CONTRIBUTING.md`** with quick-links to atlas/buff/preload paths. `entity-name-submission.yml` label reconciled from `atlas-submission` to `community-pack`. `resources/poe2-data/relabel-atlas-issues.sh` (idempotent + `--dry-run`) migrates existing open issues so nothing gets orphaned. New CI workflow rejects internal-tooling paths from leaking into any public surface.
- 🎉 **Credit block emission in `merge_community.py`.** Now fetches `body,author,number,url` per issue and emits a paste-ready markdown credit block at end-of-run, with `@unknown` fallback for deleted-account submissions, deterministic sort, and an idempotency test. Correction: `cloudflare-worker/README.md` no longer claims auto-close behavior it never had.
- 📕 **`merge_atlas_packs.py` deprecated (SL #16 Path B).** `merge_community.py` becomes the single merge rail. Deprecation banner on invocation + `docs/CONTRIBUTING-atlas.md` redirect stub. Legacy script preserved for reference / re-fold of old issues.

### Fixed

- 🗺️ **`/map` terrain-race fix.** A Discord tester reported polylines + entities visible but a fully black map. Root cause: race between the first SSE sample carrying the area code and the world-thread's terrain callback becoming ready — `/api/map` returned `{"ready":false}` which prior `map.js` treated as a permanent mismatch (`data.areaHash` undefined, `!== area`, `return null`). `fetchTerrain` now polls `/api/map` at 250 ms intervals for up to 5 s while the server reports `ready:false`, then builds the canvases when the payload arrives. Zone-out during poll cancels via a token check so we don't burn network on a stale area. Latent since v0.20.0 shipped the terrain callback path; v0.21's per-tick world work likely shifted the timing enough to lose the race consistently. Wire contract locked by 4 new C# tests.
- ⏱️ **CI 30Hz cadence test is env-aware.** GitHub Actions Windows runners sometimes hit 18 Hz under shared-VM load — env-aware bounds (15-99 on CI, 81-99 locally) keep the tight local regression signal while accepting VM jitter on CI. Test infra only; no runtime change.

## [0.20.1] — 2026-07-07
### Changed — 🧹 **Roadclearing** *(v0.20.0 review shelf drained + browser-view substrate deepened)*
- 🩺 **SseChannel heartbeat race closed for good.** The v0.20.0 T3 plan-mandated race between last-subscriber teardown and new-subscriber add is now impossible — both paths lock `_latestLock`. Publish contention is negligible; add/remove are rare. Loops that leaked one 15s ping under contention now don't.
- 🗺️ **Delta entities on `/stream`.** After the first full snapshot per subscriber, subsequent SSE messages emit `{add, upd, del}` in `entitiesDelta`. Payload shrinks meaningfully in heavy Breach / Ritual moments; sets up multi-viewer party overlays. Backward-compatible: v0.20.0 clients keep working; new `map.js` merges deltas into a persistent `Map<id, entity>`.
- 🛤️ **Path polylines land on `/map`.** The atlas/nav routing already computed by the native overlay now surfaces on the browser minimap as pathBlue polylines. New `/api/paths` route + `paths` SSE field. Layer 5 in the z-order (between fog and landmarks).
- 🌐 **Configurable update URL + in-app RC channel.** New ⚙️ Settings → Auto-Update controls: **UpdateChannel** (`stable` = `/releases/latest`, `preview` = newest GitHub prerelease) and **UpdateUrl** (custom mirror override, e.g. Gitee). SHA-256 verification + one-generation rollback + crash-loop threshold all unchanged. Mainland/VPN users unblocked; tester lane opened.
- 🧠 **Migration-guard consolidation.** 11 one-shot bool fields in `RadarSettings` (`AtlasArrowsSeeded`, `SeedLandmarksOnce`, …) collapse into a single `AppliedMigrations: List<string>`. Your existing `config/radar_settings.json` migrates transparently on first load — no state loss, no seeds re-firing. Stops the settings model growing linearly with every future one-shot seed.
- 🎨 **Dashboard hygiene.** `.card-title` CSS finally defined — the Session panel stops rendering unstyled. Palette drift on three inline colours (`#f66`, `#4a525c`, `#1a1a1a`) fixed via CSS custom properties. Settings search no longer leaks matches from collapsed cards.
- 🐭 **Cosmetic + correctness sweep.** GPS toggle keydown handler skips `<input>` / `<textarea>` / `e.repeat`. Duplicate GPS-mode init line removed. `map.js` clearRect + veil use consistent CSS-pixel dims on HiDPI. `findBracket` comment matches code. Dead port-counter statics dropped. `onZoneChange` guards concurrent invocations. `Self == el` liveness guard drops recycled-slot phantom markers (audit-2026-06-22 §4). `Poe2Atlas.ReadRegion` 1 MiB buffer hoisted to a reused field (audit-2026-06-27). `/stream` assertion added to the only-obs route-gate test.
- 🩺 **Vitals offsets re-validated for the current PoE2 patch.** The only real TODO in `src/` closed; README badge held / bumped as the Research probe reports.
- 🎯 **Blank `/map` regression closed.** A narrow race where `window.innerWidth` was `0` at page load could leave the canvas backing store 0×0 — HUD paints fine, everything else stays black. `resizeCanvas` now retries on the next `rAF` until dims are non-zero, `lerpPose` recovers from NaN endpoints, and `frame()` skips draws when the canvas or `pose.player.x/y` isn't ready. Reported by a Discord tester ❤️.
- 🛡️ **100% read-only.** Zero new memory reads. Every feature respects the zero-cost-when-off contract.

## [0.20.0] — 2026-07-06
### Added — 🖥️ **Web Views v2: Native-Feel** *(monitor-refresh `/map` + `/obs`, opt-in, default off)*
- 🖥️ **`/map` and `/obs` now render at your monitor's refresh rate with 1:1 in-game visual language.** Point any browser (second monitor, tablet, capture PC) at `http://<your-ip>:7777/map` or `/obs` and you get the same rings, chevrons, terrain mask, off-screen arrows, POIs and landmarks the overlay draws — at the same cadence, laid out the same way. **Opt-in** in ⚙️ Settings → Streaming; **default is off** so nothing new touches the network unless you flip it on.
- 📡 **New `/stream` SSE endpoint pushes snapshots at 30 Hz.** Server-Sent Events replaces polling for the map surface — the browser stops asking "any change?" ten times a second and just listens. Lower CPU on the overlay side, lower jitter on the browser side, and the render loop can finally keep up with the game.
- ⚡ **Multithreaded `HttpListener` + gzip on the big payloads.** Request handling is no longer single-file; `/api/map`, `/api/atlas`, and `/landmarks` now negotiate gzip so slower Wi-Fi links (phones, tablets on 2.4 GHz) get the same responsiveness as your desk.
- 🎯 **`/stream` entity cap raised 600 → 800 per snapshot.** In heavy Breach / Ritual / Delirium moments the browser view no longer clips extra mobs off the edge of the snapshot before you can see them.
- 🩸 **Monolith reward icons now surface to the browser views.** The reward panel you already have on the in-game overlay is mirrored to `/map` / `/obs`, so a co-pilot watching your second screen sees the same choice you do.
- 🛡️ **100% read-only.** Every one of the above ships zero new memory reads and zero new input paths. The 60 Hz web renderer is pure math over data POE2GPS already reads for the in-game overlay — same data, more screens.

### Changed
- 🧰 **Legacy `MapPageHtml.cs` / `ObsOverlayHtml.cs` retired.** Browser assets now ship as embedded resources — smaller diff surface, cleaner rebuilds, no behavior change for viewers.
- 📚 **Tencent CN client compatibility — recon design doc published.** Not a shipped feature yet; the design notes (`2026-07-06-v0.20.0-map-60hz-clone-design.md`) document what the CN client looks like structurally so a future release can support it cleanly.

## [0.19.6] — 2026-07-02
### Fixed — 🧭 **Off-screen Atlas arrows are back — and now they point true**
- 🧭 **Off-screen Atlas arrows restored, accurate.** v0.19.5 turned them off because PoE2 stops updating a node's on-screen position the moment it scrolls off-screen, so the old arrows aimed at stale/garbage coordinates ("ghost arrows to nothing"). POE2GPS now derives each off-screen arrow's direction from the target node's **stable grid coordinate** instead: it fits the grid→screen mapping from all the nodes currently **on**-screen (whose positions are valid) and uses that to place any off-screen target reliably. So your tracked Citadels/maps get a border arrow that actually points at them, and it stays correct as you pan.
- 🎯 Arrows follow the **same per-tag rules** as before (⚙️ Settings → Atlas) — no new toggle. On-screen node **rings, routes, and chevrons are unchanged**. If the view is too sparse to fit the mapping, off-screen arrows simply don't draw that frame (never a ghost). Still **100% read-only** — pure render-side math over data already read.

## [0.19.5] — 2026-07-02
### Changed
- 🧭 **Off-screen Atlas arrows turned off** (the "ghost arrows pointing at nothing"). PoE2 stops updating a map node's position the moment it scrolls off-screen, so any arrow toward an off-screen target was aiming at stale/garbage coordinates — there's no reliable way to point at it from the off-screen position. On-screen node **rings are unchanged**. Bringing off-screen arrows back *accurately* (using each node's stable grid coordinate instead of the unreliable position) is a planned follow-up.

## [0.19.4] — 2026-07-02
### Fixed
- 🧭 **No more "ghost" Atlas arrows pointing at nothing.** PoE2 stops updating an Atlas node's position the moment it scrolls off-screen, so arrows toward far-off tracked maps/Citadels were aiming at stale/garbage positions — arrows to empty space. The overlay now suppresses an arrow whose target projects to an implausible distance (the tell-tale of a culled node's junk position), so you only see arrows that actually point at something. On-screen node rings are unchanged. (Accurate arrows to *never-seen* distant nodes need a grid-based follow-up — those were the ghosts, and never pointed correctly.)

## [0.19.3] — 2026-07-02
### Fixed
- 🖥️ **No more freeze/crash when you click or select text in the console.** Windows "Quick Edit" mode pauses a console app's output the instant you select text — which froze POE2GPS (it stops reading the game) and looked like a crash, especially when trying to copy a diagnostic line. Quick Edit is now disabled, so interacting with the console window never freezes the overlay.
- 🩸 **Energy Shield read corrected for the current patch** — the ES vital offset drifted (`0x248 → 0x264`) and is now updated. (POE2GPS already self-heals per-user offset drift, so ES kept working — this just makes the built-in value correct and silences the drift notice.)
### Added
- 📝 **`config/poe2gps.log`** — everything printed to the console is now also written to a log file next to the exe, so diagnostics are easy to copy and report, and any unhandled error writes a full stack trace there. Bug reports just got a lot more actionable.
- 🙏 Thanks to **Diamondsr** for the reports + diagnostics that drove these fixes (added to Credits).

## [0.19.2] — 2026-07-02
### Fixed — 🧭 **Atlas markers piling up at the top**
- 🧭 **Fixed Atlas node markers / route arrows piling up at the top of the screen** instead of sitting on their nodes. This was a regression from the v0.18.0 Stealth-Reads pass: the Atlas overlay could **freeze its layout on an indeterminate view** (most often right after the new auto-updater relaunched the app, before the window size was known) and hold stale positions. It now never freezes on an unresolved view and always uses live node positions.
- 🔬 **Reads were never the problem.** An in-game diagnostic confirmed the memory reads, offsets, and panel detection were all correct on 0.5.4 — this was purely overlay render logic. Still **100% read-only**. Huge thanks to the community members who reported it and ran the diagnostic. ❤️

## [0.19.1] — 2026-07-02
### Added — 🔄 **Silent Auto-Update** & 🧭 **True-North Map**
- 🔄 **POE2GPS updates itself.** When a newer release exists on our GitHub, POE2GPS downloads it in the background, verifies it (**SHA-256**), and installs it on your next launch — no more hunting for a zip. Fully **opt-out-able**: **⚙️ Settings → Auto-Update → Silent / Notify only / Off**.
- 🛡️ **Safe by design.** Downloads only from `github.com/luther-rotmg/POE2GPS` over HTTPS, verifies the checksum before installing, swaps only `Overlay.exe`, and **never touches your `config/` or `icons/`**. Keeps `Overlay.old.exe` for one generation and auto-rolls-back if a new build fails to start. No telemetry, no pricing — still 100% read-only of the game.
- 🧭 **Web minimap now matches the game.** The `/map` view renders **isometrically**, aligned with the in-game overlay instead of the old top-down/rotated look — with a one-tap **iso ↔ top-down** toggle.
- 📄 **`CHANGELOG.md` now ships next to the exe** (and lands beside it on auto-update).

## [0.19.0] — 2026-07-01
### Added — 🩸 **Buff Icons** *(opt-in — see what dangerous buff an elite is running)*
- 🩸 **Know why that rare just got scary.** When enabled, POE2GPS reads the **active buffs on elite monsters** (Rare / Unique / Boss) and floats short **tier-colored tags below the mob** — a **fire/cold/lightning aura**, **enrage**, a **shield**, **haste**, a temporal bubble, etc. — with a **countdown** for temporary ones. Now you can *see* the empowering aura before it deletes you.
- 🎚️ **Curated + self-growing.** A built-in catalog maps the combat-relevant buffs to a readable name + **danger tier** 🔴 *Deadly* · 🟠 *Notable* · 🔵 *Minor*; anything uncatalogued is auto-tiered by heuristic and prettified, while pure engine-noise buffs are suppressed. A **"Display ALL" diagnostic** + an **observed-buffs panel** in the dashboard let you (and the community) grow the catalog from real fights — same approach as affix nameplates and Preload Alert.
- 🎛️ Tune it in **⚙️ Settings → Buff Icons**: enable, danger tier, per-rarity (Rare/Unique/Magic), max tags, show-all.
- 🥷 **Stealth-first, off by default.** When off it reads **nothing** — buff reads are gated on the feature and only run for elites you're near, and each buff's id is cached. Fully in line with the Stealth Reads pass.
- 🛡️ **100% read-only.** One new (patch-validated) memory read, **no input, no pricing, no writes**. Tags render through the same camera projection as HP bars / affix nameplates.

## [0.18.0] — 2026-07-01
### Changed — 🥷 **Performance v3: Stealth Reads** *(read the game less, see exactly the same thing)*
- 🩶 **The overlay now reads the game's memory far less often** — a smaller footprint and less CPU, with **zero change to anything you see**. Every dot, HP bar, nameplate, arrow, route, and the atlas behave identically; the only thing that moves is the **`reads/sec` counter** (watch it drop live in the dashboard / `⚙️` status).
- 🎛️ **Reads now scale with the features you actually use.** If a feature is **off**, the overlay stops reading the data that fed it — so a lean setup reads dramatically less. Affix nameplates off → no monster-mod reads. Ground-item overlay off → no dropped-item reads. Atlas content-icons / auto-route / hide-filters off → those per-node reads stop. (All fail-safe: anything a feature needs is always read.)
- 🌌 **Atlas got the biggest cut.** While the Atlas is open it used to re-scan **~20,000 memory reads *every tick*** — even sitting still. Now the static per-node data is **cached**, off-screen nodes are **culled** before reading, and the current-node poll is slowed to ~1/s. Panning, routing, rings, arrows, and content icons look exactly the same.
- 😴 **Idle when you're away.** While **PoE2 isn't the focused window** (alt-tabbed), the overlay stops its per-frame reads entirely (it isn't drawing anyway) and picks right back up on focus. Streamers/dashboard-watchers with "always-show" on are unaffected.
- ⚡ **Smarter live reads.** De-duplicated redundant per-frame reads, cached values that never change (character name), and slowed reads for things that change slower than the eye (level, %-vitals, POI completion, monolith data) — every one imperceptible.
- 🛡️ **100% read-only, no new offsets.** This release only *removes* reads; it adds nothing. Fully compliant, and every feature verified unchanged.

## [0.17.0] — 2026-07-01
### Added — 🛰️ **Remote Views** *(see your overlay from anywhere on your network)*
- 🌐 **Remote Access (LAN)** *(opt-in — off by default)* — flip one toggle and the overlay's pages become reachable from **other devices on your network**: open `http://<your-ip>:7777/obs` on your **stream-capture PC**, or `…/map` on a **phone / tablet / Raspberry Pi**. **Writes stay locked to your machine** — a LAN device can **view**, but **nobody on your network can change your settings** (every settings write is still loopback-only; LAN peers get a `403`). Needs an **app restart** to apply, and Windows will ask to allow POE2GPS through the **firewall** the first time. The dashboard shows your live **LAN URLs** once it's on. *(Reads are unauthenticated over your LAN by design — only enable it on a network you trust.)*
- 🗺️ **Web minimap** — a brand-new standalone page at `http://localhost:7777/map`: a clean **top-down radar** of the **walkable terrain + live dots (monsters by rarity · POI · friendlies) + your position**, centred on you and updating live. **Drop it fullscreen on a second monitor, a phone, or a Raspberry Pi below your main screen** and stop tabbing the in-game map open. Zoom with **+ / −**. Costs **nothing when nobody's viewing it** — it only does work while a browser has it open. Pair it with Remote Access above to run it on that Pi. 🥧
- 🛡️ Both are **100% read-only** of the game — **no new offsets, no new memory reads**, no input, no pricing. They're just new *views* of data the overlay already reads; both default to **off / zero-cost**.

## [0.16.0] — 2026-06-30
### Added — 📡 **Streaming & Presence** *(two ways to share your session)*
- 🎥 **OBS overlay** — a **transparent, stream-styled page** at `http://localhost:7777/obs`. Add it as a **Browser Source** in OBS and your session stats composite right over gameplay: session/zone timers, area + level, **kills** (N·M·R·U), **maps/hr**, **XP-efficiency**, next objective. Pick which widgets show + colour/opacity/scale/corner in **⚙️ Settings → OBS Overlay**. Built on the data the overlay already publishes — no new reads.
- 🎮 **Discord Rich Presence** *(opt-in — off by default)* — show your PoE2 run in your Discord status: **`{area} · Level {level}`**, maps/hr, an **elapsed timer**. **You write the templates** (tokens `{area} {level} {zones} {mapshr} {kills} {xpeff}`), and it runs under a **neutral app identity** — friends see your progress, not "an overlay tool." Publishes **only to your local Discord**, on its own thread so it never touches the game loop.
  - *One-time setup:* paste a Discord **Client ID** in **⚙️ Settings → Discord Rich Presence** to activate it (blank = inert).
- 🛡️ Both are **100% read-only** of the game — no new offsets, no new reads, no input, no pricing. OBS stays on localhost; Discord RP is your explicit opt-in.

## [0.15.0] — 2026-06-30
### Added — 🧭 **Situational Awareness** *(two awareness upgrades)*
- ➡️ **Off-screen entity arrows** *(on by default; seeded for Uniques + Bosses)* — when a notable monster is **off the edge of your screen**, an **arrow at the window border points right at it**, colour-matched to its rule — so you spot the unique/boss/pack **before it comes into view**. It's driven by your existing **display rules**: flip the new **"off-screen arrow"** checkbox on any rule to include it. Nearest-first with a **cap** so dense packs stay readable. Tune size / label / max in **⚙️ Settings → Entity Arrows**.
- 📊 **Session HUD v2** — three new opt-in lines for the run tracker:
  - 💀 **Kills (observed)** — a live tally by rarity (**N · M · R · U**), counted from monsters you watch die. *(Honest by design: it counts the kills it witnesses, so huge off-screen AoE clears read a touch low.)*
  - 🗺️ **Maps/hr** — your map-zone throughput (town/hideout trips don't count).
  - 📈 **XP-efficiency** — `your level − area level` (e.g. `+3` over-levelled · `−5` under-levelled), at a glance.
  - Toggle them in **⚙️ Settings → Session HUD**; reset with **Ctrl+Alt+R** like the rest.
- 🛡️ Both are **100% read-only** — built entirely on data the overlay already reads (**no new offsets**, no new reads). No input, no pricing.

## [0.14.0] — 2026-06-30
### Added — 🔮 **Preload Alert** *(opt-in — off by default · experimental)*
- 🔮 **Know what's in the zone the moment you load in.** When you enter an area, POE2GPS reads the list of assets the game just loaded and calls out the **notable content waiting for you** — **pinnacle bosses** (Arbiters · Xesht · Kosis · Omniphobia · …), **league encounters** (Breach · Ritual · Expedition · Abyss · Incursion · Delirium · …), **Rogue/Conqueror exiles**, and **valuable chests** — as a tidy **corner panel**, tier-coloured 🔴 *pinnacle* · 🟠 *high* · 🟡 *mechanic* · 🔵 *interactable*.
  - 🧠 **Self-tuning noise filter** — the game always keeps a lot of assets resident, so a naive read would flag *everything*. POE2GPS learns which paths show up in **every** zone (base noise) and **suppresses them**, surfacing only what's *genuinely this zone*. Tune the aggressiveness (**common-noise threshold** + **warm-up zones**) yourself.
  - 🎚️ **Your call** — a **minimum tier** to display, an optional **audio cue** when *(≥ a tier you pick)* content loads, corner **anchor + offset**, and a **🔬 Diagnostic view** in the dashboard that shows every matched path with its zone-frequency, so you (and the community) can help grow the catalog.
  - Flip it on in **⚙️ Settings → Preload Alert (experimental)**. **100% read-only** — it only *reads* the asset list the game already loaded and draws text. No prices, no trade, no input. 🛡️
### Changed
- 🧰 **Dev tooling** — new `--preload` Research probe (one-click launcher) that validates the loaded-files reader live per patch.

## [0.13.0] — 2026-06-29
### Added — 🌌 **Atlas QoL** *(a 7-part upgrade to the Atlas overlay)*
- 👁️ **Content icons on fogged maps** *(on by default)* — the game hides a map's content art until you reveal the tile. POE2GPS now stamps the **content glyph** (🌀 Breach · 💀 Boss · 🔮 Essence · ⛏️ Expedition · 🩸 Ritual · 📦 Strongbox · …) **right on the fogged node**, so you can see *what's out there* **before** committing a single point. 15 crisp built-in icons; size + on/off in **Settings**.
- 🎯 **Built-in Map Targets** *(seeded once, additively)* — a fresh install now **highlights the maps that matter out of the box** — every **Citadel**, the **Halls**, and key uniques — ring + route + off-screen arrow, zero setup. Purely additive: your own rules are never touched.
- 🎨 **Colour groups** — define a named, coloured set (e.g. *Citadels* → gold, *Uniques* → orange) and **every member map recolours together**. Add / edit / remove groups live from the **Atlas** dashboard tab.
- 🧹 **Hide filters** — **Hide completed** *(on)* sweeps run maps off the overlay, and **Hide accessible-only** *(off)* declutters the adjacent frontier — so the Atlas shows what's *left to do*, not what's done.
- 🗂️ **Data-driven map intel** — a bundled GGG-data layer resolves each node's **display name · type · content tags** (`unique` · `lineage` · `arbiter` · …), powering a new **Type** filter axis and richer tooltips.
- ➡️ **Directional route chevrons** — auto-routes and your **F10** path now draw **arrowheads** showing travel direction; overlapping routes **interleave** so each stays readable.
- 🩹 **Off-screen route fix** *(correctness)* — route segments no longer **jitter / wobble** when a node sits off-screen (off-screen positions are noisy, so those segments are now cleanly culled).
- 🛡️ **Still 100% read-only.** Everything above only **reads** atlas memory the game already exposes + bundled static data, and **draws**. No pricing, no trade, no input — same compliance bar as always.

## [0.12.0] — 2026-06-29
### Added
- ✨ **Affix nameplates** *(opt-in — off by default)* — see an elite monster's dangerous **modifiers floating right above its head**, on screen, **no mouse hover needed**. Each rare/unique shows *its own* affixes, color-coded by danger.
  - 🎯 **Danger tiers** — a curated masterlist turns raw ids into readable names (`MonsterPhysicalDamageAura1` → "Physical Damage Aura") and ranks them **Deadly · Notable · Minor**; anything uncurated is auto-prettified, so nothing is ever missed.
  - 🎛️ **Customizable filters** — pick a **tier threshold** (Deadly only · Deadly + Notable · All), add per-affix **Always-show / Hide** overrides, or flip **Display all** to show every affix. Choose which rarities count (Rare / Unique / Magic).
  - Turn it on in **Settings → Affix nameplates** (ships collapsed). 100% read-only — it only reads mods PoE2 already exposes and draws text; never sends input.
### Fixed
- 🔧 **RuneStation offsets** re-validated for the 2026-06-25 patch (folded from upstream) — runeshape-monolith reads were stale (`ListenerSub 0x98→0xA0`, `RuneStride 0x6c→0x68`).

## [0.11.0] — 2026-06-28
### Changed
- **Performance v2 — fewer memory reads per tick** (still strictly read-only; *no change to what the overlay reads or draws*): a second optimization pass aimed at ReadProcessMemory syscalls/sec on the world hot path.
  - **One bucket read per new monster instead of ~6** — each entity's components (render / position / life / rarity / minimap-icon / chest) now resolve in a single pass. The biggest per-pack reduction.
  - **Cached, slowly-refreshed hostility** — friend/foe is read once per monster and re-checked about once a second (so enemy↔friendly conversions still flip), instead of every tick — a large saving on dense maps.
  - **Cached opened chests**, **one bulk read for the minimap element** (was 5 reads/frame), **bulk mod reads**, and the world thread's **player vital-offset latch** no longer re-reads three vitals every tick.
  - **Fewer allocations** — the per-tick entity list is reused (triple-buffered), the terrain unpack buffer is pooled via `ArrayPool`, and mod de-duplication is now O(n).
### Added
- **`rpmPerSec` in `/state`** — a live reads/sec readout (next to `worldMs` / `renderMs`) so you can watch the footprint yourself.

## [0.10.0] — 2026-06-28
### Added
- **Custom keybinds** — remap the keyboard hotkeys (F6/F7/F9/F10/F12 and the Ctrl+Alt cycle/menu/reset binds) from a new **Keybinds** card in Settings. Still 100% read-only — the overlay only *reads* the keys you choose, never sends input. (Controller R3/L3 and the slot-jump digits stay fixed.)
- **Map-mechanic intelligence** — the Zone summary panel now also counts nearby **league mechanics** (Strongbox, Shrine, Breach, Expedition, Ritual, Essence), and there's a new optional **"mechanic nearby" audio cue**.
- **First-run quick-start** — a welcome card with the essentials, a hotkey cheat-sheet, and a one-click **"Apply recommended setup"**; dismissible, re-openable any time.
- **Settings search & collapsible cards** — a search box filters the Settings cards, and each card collapses (state remembered) so the growing options list stays navigable.

## [0.9.1] — 2026-06-28
### Added
- **Audio: volume slider + per-event tone picker** — set the alert volume and choose a distinct tone (Chime / Bell / Ding / …) for each event, with a Test button to audition.
- **Preset library** — the Presets card is now a real library: **built-in starter presets** (High-contrast, Minimal, Boss & unique hunter) you can apply in one click, plus **save / name / apply / delete your own** local presets (on top of the existing share-code + file import/export).
- **Zone summary panel** *(opt-in, off by default)* — a compact overlay panel with live counts for the current zone (rares · uniques · chests · exits), anchorable to any corner; its toggle sits prominently at the top of Settings.
### Changed
- The in-app console hotkey banner now lists **Ctrl+Alt+R** (reset Session HUD counters), matching the README.

## [0.9.0] — 2026-06-27
### Added
- **Audio alerts** *(off by default)* — short, distinct procedurally-generated tones for three high-signal events, each toggleable from a card at the top of Settings: a **rare/unique monster** comes into range, a **unique item** drops, or you **reach your active objective**. A "Test" button auditions each tone. Output only — no input is ever sent to the game.
- **Community presets** — share your radar's *look* (display rules + icon / HP-bar / terrain styles) as a copy-paste **share-code** or a `.poe2preset` file, and import one to adopt someone else's setup instantly. Imports are sanitized + size-bounded, only touch visual config (never operational/anti-detection settings), and auto-save a backup of your current look first.
### Changed
- GitHub Release notes now populate automatically from this changelog.

## [0.8.0] — 2026-06-27
### Changed
- **Performance & footprint pass** — a head-to-toe optimization sweep, with **no change to what the overlay reads** (still strictly read-only):
  - **~15–20 MB lower idle RAM** — the mod-translation tables now load only when the (default-off) gear scorer actually needs them, instead of eagerly at startup.
  - **Fewer per-frame allocations at high refresh rates** — atlas projection/route geometry, session-HUD text, entity/landmark colors, and the monolith panel are now cached/reused per frame instead of rebuilt every frame.
  - **Lighter world tick** — objective ranking is computed once per tick, the Objective Director reconciles at ~4 Hz (forced on zone change), nav-target building is single-pass, and area hash/level are cached per zone.
  - **Capped session logs** — the seen-POI, entity-atlas, and mod catalogs no longer grow unbounded over a long session.
### Fixed
- A latent data race where the render thread read player vitals through the world thread's memory reader — vitals now use the render thread's own reader. Vital-offset detection also re-validates if it ever latches onto a bad read (e.g. a torn loading-screen frame).

## [0.7.1] — 2026-06-27
### Added
- **Force re-scan** button (Status card) + `POST /api/rescan` — re-detect the game after a patch without restarting.
- **Health pill** in the dashboard masthead — game read-state (in game / connecting / out-of-date) on every tab.
- Controller bindings (R3/L3) in the console hotkey list; the version is now prominent.
### Changed
- WorldLoop clears the radar + rate-limits the log if a tick throws (no stale data / log flood).
- Removed dead/INVALID unreferenced offset stubs from the offset table (internal hygiene).

## [0.7.0] — 2026-06-27
### Added
- **Campaign GPS** (experimental, off by default) — cross-zone campaign navigation: routes you toward the next critical-path zone's exit, shown on the dashboard Zone Plan + overlay.

## [0.6.0] — 2026-06-26
### Added
- **Patch-resilience & health/status** — the overlay self-detects when a PoE2 patch breaks the offsets, starts non-fatally and self-connects (works when launched at login), re-attaches after a game restart, and shows a clear update-aware banner + dashboard Status panel instead of failing silently.

## [0.5.2] — 2026-06-26
### Changed
- Target cycling follows the radar-menu order by default; "Intelligent target cycling" (priority/distance) is an opt-in toggle. Hold-to-fast-cycle on controller + keyboard.

## [0.5.1] — 2026-06-25
### Fixed
- PoE2 **0.5.4** offset hotfix (AreaInstance +0x18 insertion: LocalPlayer/ServerData/entities/terrain re-validated).

## [0.5.0] — 2026-06-24
### Added
- Objective Director v2: tier-aware ranking + Zone Plan + a suggest-only classifier. Plus stealth `.exe` cleanup and atlas node-centering fixes.

## [0.4.0] — 2026-06-23
### Added
- Session HUD (opt-in, off by default): pace / zone context / deaths, on the overlay + dashboard.

## [0.3.2] — 2026-06-24
### Added
- In-app Discord link in the dashboard tab bar and console banner at startup.

## [0.3.1] — 2026-06-24
### Changed
- One-click Contribute is now live: the collector is deployed; the Contribute button uploads directly with no user setup required.

## [0.3.0] — 2026-06-23
### Added
- Community Pipeline: Cloudflare Worker collector with auto-filter (profanity/junk/identifying/oversized) and GitHub issue filing for maintainer review. `merge_community.py` for merging approved packs.

## [0.2.2] — 2026-06-23
### Added
- Richer classify labels: curated grouped vocabulary (~40 labels, autocomplete) served from `/api/labels`; custom typed labels still accepted.

## [0.2.1] — 2026-06-23
### Added
- **Dynasty-support map highlighting**: Sealed Vault / Sacred Reservoir / Derelict Mansion ring purple + label + auto-route on the Atlas (first community request).

## [0.2.0] — 2026-06-23
### Added
- Console glow-up: POE2GPS ASCII banner, color-coded startup, and full hotkey reference in the cmd window.
- Community Contribute (pipeline groundwork): one-click entity name + label upload.

## [0.1.9] — 2026-06-23
### Added
- **Gear Scorer v2**: meta-derived starter weights out of the box; stat-ID chips on every affix; rarity colors + grid heatmap.

## [0.1.8] — 2026-06-22
### Added
- **Quick-Target Cycler**: Ctrl+Alt+]/[ cycles radar targets next/prev; Ctrl+Alt+1–9 jumps to a slot; controller L3/R3 support.

## [0.1.7] — 2026-06-22
### Added
- **God-Roll Detector** (experimental): per-affix roll quality and tier badge on identified gear; affixes at ≥ 90 % of max highlighted gold.

## [0.1.6] — 2026-06-22
### Changed
- Footprint & cleanup: reduced binary footprint, stealth process name, miscellaneous robustness fixes.

## [0.1.5] — 2026-06-22
### Added
- **Map the game together**: community entity pack import/export (`atlas-pack.json`) for sharing discovered entity names and labels.

## [0.1.4] — 2026-06-22
### Changed
- Stealth & robustness: process randomization on launch; no stray `.exe` left on exit; hardened attach loop.

## [0.1.3] — 2026-06-22
### Added
- **Entity Atlas**: dashboard tab for naming and classifying discovered entities/POIs; names show live on the radar.

## [0.1.2] — 2026-06-22
### Added
- **Catalog Builder**: dashboard Director tab for adding uncatalogued POIs/landmarks to the Objective Director catalog.

## [0.1.1] — 2026-06-22
### Changed
- Minor fixes and stability improvements over the initial release.

## [0.1.0] — 2026-06-21
### Added
- Initial release: read-only PoE2 navigation overlay with radar, atlas projection, Objective Director, and HTTP API.
