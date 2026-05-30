# POE2Radar — Contributor Guide

External memory-reading **map/radar overlay for Path of Exile 2**. .NET 10, Windows, x64 only.
Reads game state out of process (no injection) and draws an overlay; an opt-in auto-flask feature
sends keystrokes. Forked from a PoE1 framework, since rewritten around the live PoE2 layout.

## Non-negotiable rules

**PoE2, not PoE1.** Offsets are PoE2-specific and drift with patches. Validated values live in
`Game/Poe2Offsets.cs`; `resources/community-offsets.md` documents them + the discovery workflow.

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
- `Pathfinding/MapProjection.cs` + `GridConstants.cs` — isometric grid→screen projection and the
  grid↔world scale (250/23 ≈ 10.87).

**Overlay** (`src/POE2Radar.Overlay/`):
- `RadarApp.cs` — tick loop. Render rate (~144 Hz): live player + render. World rate (~30 Hz):
  refresh entities/terrain/landmarks. Publishes a `RadarState` for the API; runs auto-flask.
- `Overlay/OverlayWindow.cs` — per-pixel-alpha layered window (`UpdateLayeredWindow`), tracks the
  game window. `Overlay/OverlayRenderer.cs` — Direct2D: terrain bitmap + entity dots (colored/sized
  by rarity) + POI rings + landmark markers + player blip + HUD. Drawn only when PoE2 is focused.
- `Overlay/TerrainBitmap.cs` — bakes the walkable grid into a bitmap, rebuilt per area.
- `Web/ApiServer.cs` — read-only HTTP API on `localhost:7777` (`/state`, `/entities`, `/landmarks`).
- `Input/SendInputNative.cs` — scancode `SendInput` for auto-flask.

**Research** (`src/POE2Radar.Research/Program.cs`) — probes: `--hp` (value-scan), `--chain`,
`--entity`, `--find`/`--find-entities`/`--find-terrain`/`--find-map`, `--tiles`, `--rarity`,
`--info`, `--watch` (area-change logger), `--dump`.

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
- **Still TBD:** camera world→screen matrix (for world-space nameplates); friendly area Name string.

## Dependencies
- `Vortice.Direct2D1` (overlay rendering). Targets `net10.0-windows`, x64.
