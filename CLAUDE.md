# POE2Radar — Contributor Guide

External memory-reading **map/radar overlay for Path of Exile 2**. .NET 10, Windows, x64 only.
Reads game state out of process (no injection) and draws an overlay; an opt-in auto-flask feature
sends keystrokes. Forked from a PoE1 framework, since rewritten around the live PoE2 layout.

## Non-negotiable rules

**PoE2, not PoE1.** Offsets are PoE2-specific and drift with patches. Validated values live in
`Game/Poe2Offsets.cs` (marked `✓` when confirmed live); re-discover via the `POE2Radar.Research` probes.

**Stay external.** Memory access via `OpenProcess` + `ReadProcessMemory`. **Never** inject into the
PoE2 process — no DLL injection, no function hooking, no packet manipulation.

**Input/automation (opt-in).** The overlay may send keystrokes via `SendInput`
(`Input/SendInputNative`) for auto-flask only. Rules: foreground-gated (only when PoE2 is focused),
in-game-gated, per-action cooldowns, master kill-switch hotkey (F8). Keep automation minimal and
clearly gated — a personal QoL tool, not a headless bot.

**Offset discovery lives in Research.** The overlay just reads; reverse-engineering/probes live in
`POE2Radar.Research`. When a patch breaks reads, run the Research probes, re-validate, commit.

**Three-pillar layout.** Exactly three projects:
- `src/POE2Radar.Core` — memory plumbing + the PoE2 offset table + the live read layer. Read-side.
- `src/POE2Radar.Overlay` — tick loop, Direct2D overlay, HTTP API, opt-in input. The deliverable `.exe`.
- `src/POE2Radar.Research` — dev-time discovery/validation tooling. Never linked into the overlay.

## Architecture

**Entry point:** `src/POE2Radar.Overlay/Program.cs` — attach (`ProcessHandle.AttachToPoE`) →
`Bootstrap.ResolveGameStateSlot` (AOB scan for the GameState pointer, validated by a working chain)
→ `RadarApp.Run`.

**Core read layer:**
- `MemoryReader.cs`, `ProcessHandle.cs`, `Native/` — Win32 + typed reads. `AttachToPoE` lists the
  PoE2 client process names.
- `Game/Poe2Offsets.cs` — **single source of truth for all PoE2 offsets** (validated + GameHelper2-
  sourced; markers `✓` = confirmed live).
- `Game/Poe2Live.cs` — the live reader: resolves GameState → InGameState → AreaInstance →
  LocalPlayer each tick; reads player vitals, walks the entity std::maps into categorized dots
  (rarity, reaction/hostility, POI via MinimapIcon, HP), reads the walkable terrain grid, the map
  UI element (visibility/shift/zoom), tile landmarks, and area/character info. Caches per-entity
  component addresses; cache key is the AreaInstance address (invalidates on zone change).
- `Game/GameStructs.cs` — blittable structs (`StdVector`, `Vector2/3`, `VitalStruct`).
- `Game/AobScanner.cs` + `AobPatterns.cs` — pattern scan for the GameState global slot.
- `Game/LifeValidator.cs` — value-scan to find the Life component by HP (Research `--hp`).
- `Game/ItemModTranslator.cs` — renders item mods (internal mod id + rolled values read from memory) to
  the game's English stat lines (e.g. `IncreasedLife5 [67]` → "+67 to maximum Life"). Two embedded RePoE
  PoE2 tables (`poe2_mod_stats.json` mod→stat-ids, `poe2_stat_descriptions.json` GGG stat descriptions,
  `poe2_mod_ranges.json` per-mod roll ranges), regenerated per patch via the `POE2Radar.Research --gen-*`
  probes (e.g. `--gen-weights`, `--gen-ranges`) from vendored RePoE snapshots under `resources/poe2-data/`
  (there is no `regenerate.py`). Validate with `--inventory --itemmods`.
- `Pathfinding/MapProjection.cs` + `GridConstants.cs` — isometric grid→screen projection and the
  grid↔world scale (250/23 ≈ 10.87).

**Overlay** (`src/POE2Radar.Overlay/`):
- `RadarApp.cs` — **two threads** (the render thread is never blocked by the heavy walk):
  - *Render loop* (`Tick`, ~`FpsCap` Hz): fast per-frame reads on its OWN reader stack (`_liveRender`) —
    live player/vitals/camera/map + auto-flask + HP-bar live pos — then draws from the lock-free
    published snapshots. Publishes `RadarState` for the API.
  - *World loop* (`WorldLoop`→`WorldTick`, ~30 Hz, own thread + reader `_live`): the heavy entity/terrain/
    landmark walk + mod catalog + HP-bar specs + item labels + atlas update + nav/route maintenance.
    Publishes an immutable `WorldSnapshot` (+ a separate `AtlasRender` bundle) the render thread reads
    lock-free (volatile reference swap, same idiom as `_state`).
  - Three INDEPENDENT reader stacks over the one `ProcessHandle` (RPM is concurrency-safe; the per-instance
    buffers/caches are NOT): `_live` (world), `_liveRender` (render), `_liveApi` (HTTP/tile scans). `_atlas`
    is internally locked, so it's shared. HP-bar live reads carry the mob's Render/Life component addresses
    in the spec (`Poe2Live.TryBarComponents`/`TryLiveBarAt`) so the render thread reads them with no shared
    cache. The render thread gates drawing of snapshot data on `snap.AreaHash == liveAreaHash` (zone-load
    guard). `/state` exposes `worldMs`/`renderMs` timers. Auto-flask STAYS on the render thread.
- `Overlay/OverlayWindow.cs` — per-pixel-alpha layered window (`UpdateLayeredWindow`), tracks the
  game window. `Overlay/OverlayRenderer.cs` — Direct2D: terrain bitmap + entity dots + landmark
  markers + world-space HP bars + player blip + HUD. Drawn only when PoE2 is focused. Icon
  shape/color/opacity/size per item, metadata-matched "mechanic" overrides, and HP-bar geometry are
  config-driven via `RadarSettings.Styles` / `.HpBars` (defaults mirror the old hardcoded look) and
  editable live in the Console Settings tab. HP-bar rarity is signaled by scaling border weight.
  Icon *shapes* are named SVGs from `Overlay/IconLibrary.cs` — built-in set materialized to an
  `icons/` folder next to the exe on first run (sibling of `config/`); any `*.svg` dropped there
  (single/multi `<path>`) overrides a built-in or adds a new icon. `Overlay/SvgPath.cs` parses each
  path `d` (M/L/H/V/C/S/Q/T/Z + A→cubic) into figures the renderer normalizes (viewBox→unit) and
  caches as an `ID2D1PathGeometry` per name.
- `Overlay/TerrainBitmap.cs` — bakes the walkable grid into a bitmap, rebuilt per area.
- `Web/ApiServer.cs` — read-only HTTP API on `localhost:7777` (`/state`, `/entities`, `/landmarks`,
  `/api/icons` — the icon library for the dashboard's SVG-preview shape pickers).
- `Input/SendInputNative.cs` — scancode `SendInput` for auto-flask.

**Research** (`src/POE2Radar.Research/Program.cs`) — probes: `--hp` (value-scan), `--vitals`
(dump the local player's Life component — what the configured Health/Mana/ES offsets read + every
valid VitalStruct in the component; the per-patch re-validation for the auto-flask pools), `--chain`,
`--entity`, `--find`/`--find-entities`/`--find-terrain`/`--find-map`, `--tiles`, `--rarity`,
`--info`, `--inventory` (player inventory + item structure: lists every inventory with box dims, dumps
each item's slot/rarity/identified/art/stack/components, `--inv N` for one inventory by id, `--itemmods`
for explicit/implicit mod ids + rolled values; self-validates the drift-prone vec hops) + `--itemdump
<hexItemAddr>` (deep single-item probe: Mods rarity/identified + per-affix id/value + Mods.dat row scan,
LocalStats statIndex→value, Sockets contents), `--watch` (area-change logger),
`--dump`, `--presence` (walk-stable before/after diff to
find a buffed scalar), `--devtree` (browser-based live memory/UI/entity explorer at
`localhost:7778` — `DevTree/DevTreeServer.cs` + `DevTreeHtml.cs`; the PoE2 stand-in for ExileApi's
DevTree), and `--atlas-probe` (one-shot ATLAS PROJECTION recovery/validation — run with the Atlas map
open after a patch: re-locates the node class + canvas, validates every offset (PASS/⚠DRIFT), and prints
the derived projection + paste-ready offsets; the `--atlas-{xform,canvas,nodes2,readnodes,corr}` probes
remain for deep re-discovery), and `--atlas-graph` (validates the node GRAPH — per-node grid coords
`AtlasNode.GridPos +0x320` + the connection-edge `StdVector` `AtlasGraph.ConnectionsVec` on the canvas
`+0x5A8`; brute-scans for both so it self-heals on drift — the basis for node-to-node atlas pathfinding).

**Atlas overlay projection** (✓ live, pan + zoom): atlas nodes are UiElements; a node's screen pos is
`screen = (UIscale × zoom) × relPos + offset` — relPos `+0x118` (read live; PAN is baked in), zoom =
node/canvas scale `+0x130` (read live), UIscale = winH/1600, offset calibrated once (F10/F11). NOT a
perspective homography. Calibration is a scale+translate RANSAC fit (`AtlasHomography`); the linear part
is rescaled by liveZoom/calibZoom each frame. See `resources/atlas-research-notes.md` "FULLY SOLVED".

## Key facts (validated live; re-verify per patch)

- Chain: AOB "Game States" → GameState → InGameState (active state) → `AreaInstance @ +0x290` →
  `LocalPlayer @ +0x5A0`.
- AreaInstance: AreaInfo `+0xA0` (code), AreaLevel `+0xC4`, AreaHash `+0x11C`, AwakeEntities std::map
  `+0x6C0` / Sleeping `+0x6D0`, TerrainStruct `+0x8A0` (walkable `+0xD0`, BytesPerRow `+0x130`).
- Entity: Details `+0x08`, ComponentList `+0x10`; component map via ComponentLookUp StdBucket.
  Rarity = ObjectMagicProperties `+0x144`; hostility = Positioned.Reaction `+0x1E0` (friendly = bit
  pattern `(b&0x7F)==1`); grid = Render world `+0x138` / 10.87; Life HP `+0x1A8` / Mana `+0x1F8` / ES
  `+0x230`; Player name `+0x1B0`, level `+0x204`.
- Map UI: UiRoot `InGameState +0x2F0`; UiElement Self `+0x08`, Children `+0x10`, Flags `+0x180`
  (visible = bit `0x0B`); MapUiElement Shift `+0x368`, DefaultShift `+0x370` (= (0,-20)), Zoom `+0x3A8`.
- Inventory (✓ live, Research `--inventory`): `AreaInstance +0x580` → ServerData → `+0x48` PlayerServerData
  vec `[0]` → ServerDataStructure → `+0x320` PlayerInventories vec (InventoryArrayStruct stride `0x18`:
  `+0x00` id, `+0x08` → InventoryStruct, `+0x10` = ptr−0x10 fingerprint). InventoryStruct: TotalBoxes(X,Y)
  `+0x150`, ItemList vec(ptr→InventoryItemStruct, len X·Y) `+0x170`. InventoryItemStruct: Item entity
  `+0x00`, Slot `+0x08`. Item = Entity; Mods rarity `+0x94`/identified `+0x90`, affix vecs Implicit `+0xA0`/
  Explicit `+0xB8`/Enchant `+0xD0` (ModArrayStruct stride `0x40`, `+0x28` → Mods.dat row → first qword →
  UTF-16 internal mod id); Stack count `+0x18`; RenderItem art `+0x28`.
- **Still TBD:** camera world→screen matrix (for world-space nameplates); friendly area Name string.

## Dependencies
- `Vortice.Direct2D1` (overlay rendering). Targets `net10.0-windows`, x64.
