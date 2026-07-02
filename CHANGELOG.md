# Changelog

All notable changes to POE2GPS. This project is a strictly read-only, GGG-compliant PoE2 navigation overlay.
Versions are GitHub release tags (`vX.Y.Z`); the in-app update checker compares against the latest.

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
