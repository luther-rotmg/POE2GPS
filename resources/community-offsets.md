# PoE2 offsets

## ✅ Validated against a live client — 2026-05-30

Probed via `POE2Radar.Research --hp <N> --mana <N>` then `--entity <ownerAddr>` on PathOfExileSteam.
All confirmed by exact ground-truth match against the live character:

- **Entity** (local player = `Metadata/Characters/<Class>/<Variant>`):
  `ItemBase.EntityDetailsPtr @ +0x08`, `ComponentList` StdVector `@ +0x10` (8-byte ptr elems). ✓
  (`Id @ +0x80` read 0 for the local player — revisit; not radar-critical.)
- **EntityDetails**: `name` StdWString `@ +0x08` ✓, `ComponentLookUpPtr @ +0x28` ✓
- **ComponentLookUp → component map**: StdBucket (StdVector) `@ +0x28`; entries are 16 bytes
  `{IntPtr NamePtr; int Index; int pad}`; component addr = `ComponentList.First + Index*8` (deref). ✓
  Player's 12 components resolved: Positioned, Stats, Pathfinding, Buffs, Life, Animated, Player,
  PlayerClass, Inventories, Actor, Render, Targetable.
- **Life component**: `Owner @ +0x8`, `Health @ +0x1A8`, `Mana @ +0x1F8`, `EnergyShield @ +0x230`. ✓✓✓
  (HP/ES current+max matched the live character exactly.)
- **VitalStruct**: `ReservedFlat @ +0x10`, `Regen @ +0x28`, `Max @ +0x2C`, `Current @ +0x30`. ✓
- **Render component**: `CurrentWorldPosition` (Vector3 X,Y,Z) `@ +0x138` ✓ (GameHelper2's 0xB8 is
  stale for Render here). Confirmed by movement: standing `(3961.68, 2668.24)` → after walking left
  after moving (X↓, Y↑). `grid = world.XY / 10.87`. The character's class label (UTF-16) sits @ +0x170.
- **WorldToGridRatio** = 250/23 ≈ 10.87 (from GameHelper2 TileStructure). ✓
- Char-name offset in Player component: TBD (not at PoE1's +0x168; non-critical for radar).

### Top-level chain — ✓ validated live

- **GameState** via the "Game States" AOB pattern (1 unique slot). ✓
- **InGameState** = first element of `GameState.CurrentStatePtr` StdVector `@ +0x08`. ✓
- `InGameState.AreaInstanceData @ +0x290` → **AreaInstance**. ✓ (target holds the local player)
- `AreaInstance.LocalPlayer @ +0x5A0` → player Entity. ✓ (== value-scanned player)

### Entity list — ✓ validated live (research goal #1)

- `AreaInstance.AwakeEntities  @ +0x6C0` — std::map (size 378 live). ✓
- `AreaInstance.SleepingEntities @ +0x6D0` — std::map (size 58 live). ✓
- StdMap = `{IntPtr Head; int Size}` (16 bytes). Node: `Left@0, Parent@8, Right@0x10, IsNil@0x19`;
  `Data@0x20` = key `{uint id}`, value `EntityPtr@0x28`. ✓ (BFS from Head.Parent)
- Entity **type** comes from the metadata path (`EntityDetails.name`): `Metadata/Characters/*` (players/
  monsters), `Metadata/NPC/*`, `Metadata/Terrain/*/Objects/ExitTransition`, `Metadata/MiscellaneousObjects/*`.
- Entity **health / targetability** via the component map (Life @ +0x1A8, Targetable component). ✓
- Filter visuals/decorations: `id < 0x40000000`. ✓ (GameHelper2 filter holds)
- GameHelper2's `0xB58` was drifted; real offset is `0x6C0`.

### Terrain — ✓ validated live (research goal #2)

- **TerrainStruct base = `AreaInstance + 0x8A0`** (GH2's `0xD20` drifted). Sub-offsets (from base):
  - `TotalTiles @ +0x18` — `StdTuple2D<long>` = (54, 48) live → 2592 tiles.
  - `TileDetailsPtr @ +0x28` — StdVector of `TileStructure` (0x38 bytes); 145152/0x38 = 2592 ✓.
  - `GridWalkableData @ +0xD0` — StdVector, 685584 packed bytes. ✓
  - `GridLandscapeData @ +0xE8`, plus two more PoE2 grid layers @ `+0x100` / `+0x118`.
  - `BytesPerRow @ +0x130` = 621 live ⇒ cellsPerRow = 1242. (GH2 had it at 0x100; PoE2 added 2 layers.)
  - Grid = 1242 × 1104 = (tilesX×23) × (tilesY×23). `TileToGrid = 23`, `TileToWorld = 250`.

### UI tree + map element — ✓ partly validated live (map-overlay gating)

- **UiRoot** = `InGameState + 0x2F0` → root UiElement. ✓ (self-referential; children are UI elements)
- **UiElement**: `Self @ +0x08`, `Children` StdVector `@ +0x10`. ✓ (GH2's 0x30/0x38 drifted)
- **MapUiElement** (large map + minimap, same vtable 0x7FF7AA5B8660): `Shift @ +0x368`,
  `DefaultShift @ +0x370` (=(0,-20)), `Zoom @ +0x3A8` (0.5 live). ✓ Found by walking the tree:
  exactly 2 elements carry DefaultShift=(0,-20); deltas match GH2 (shifted +0x70).
- **Visibility flag**: `UiElement.Flags @ +0x180`, **IsVisibleLocal = bit 0x0B**. ✓ (toggle-diff:
  large map Flags went `0x005026F1` closed → `0x00502EF1` open; XOR = 0x800 = bit 0x0B). GH2 had
  Flags@0x1B8 (drifted). Full visibility is hierarchical (AND of ancestor bits up to UiRoot).
- **Large vs mini**: of the two MapUiElements, the **large map** is the one whose state changes on
  toggle (size floats @ +0x110, populated @ +0x340) — i.e. gate terrain drawing on ITS bit 0x0B.
  The minimap element never changes on toggle. (They have separate parents, not GH2's shared MapParent.)

### Area metadata + hostility — ✓ validated live (poll-as-you-play, watcher diff across 2 zones)

- `AreaInstance.CurrentAreaLevel @ +0xC4` (int) ✓ — 27 then 32 across zones (GH2's 0xBC drifted).
- `AreaInstance.CurrentAreaHash  @ +0x11C` (uint) ✓ — per-area random (e.g. 0x54492AF8); `+0x120` is a paired seed (GH2's 0xFC drifted).
- `Positioned.Reaction @ +0x1E0` (byte) ✓ — friendly when `(b & 0x7F) == 1`. Live: player & other
  players & own summons = 1; NPCs / monsters / objects = 0. Cleanly separates **your summons from
  real enemies** among `/Monsters/` (hostile boss read 0). Doesn't split neutral-NPC from hostile
  on its own (both 0) — combine with metadata for that.

### POI source — ✓ live

Entities with a **`MinimapIcon` component** are the game's POIs (validated: Expedition encounter,
TormentedSpirit, town waypoint/NPCs). Drawn with a white ring; `poi` exposed in the API.

### Still TBD

Camera matrix (WorldData chain) for world-space nameplates; char-name offset in Player component.
Both optional for the radar. The GameHelper2 reference below is the spec; check per-build drift.

---

Authoritative source: **`resources/GameHelper2-main/GameOffsets/`** — a GameHelper2 source dump
whose offsets are **PoE2-targeted** (`GameProcessName.cs` maps the client to "Path of Exile 2";
`StaticOffsetsPatterns.cs` patterns are for the PoE2 exe). Also see `resources/research notes.txt`
(forum post describing the AOB `Pattern.cs` / `^` BytesToSkip system).

> **Status: the validated section above is live in `src/POE2Radar.Core/Game/Poe2Offsets.cs`.** The
> GameHelper2 reference below documents PoE2's struct shapes; PoE2's layout is structurally
> different from PoE1, and offsets drift per patch — re-validate against a live client (this dump
> is an unknown-age snapshot) using the `POE2Radar.Research` probes.

Markers below: address in hex, type, and the GameHelper2 file it came from.

## Client / process

`ProcessHandle.AttachToPoE` candidate names (window title "Path of Exile 2"):
`PathOfExile`, `PathOfExileSteam`, `PathOfExile_x64`, `PathOfExile_KG`, `PathOfExileEGS`.
Source: `GameOffsets/GameProcessName.cs`.

## AOB patterns (`GameOffsets/StaticOffsetsPatterns.cs`)

`^` marks the byte from which the RIP-relative int32 is read (BytesToSkip); final addr =
matchAddr + patternLen + relInt32. To find in Ghidra: press `S`.

| Name | Pattern | Finds |
|---|---|---|
| Game States | `48 39 2D ^ ?? ?? ?? ?? 0F 85 16 01 00 00` | GameState root (the `DAT_xxx` GameStates global) |
| File Root | `48 8B 0D ^ ?? ?? ?? ?? E8 ?? ?? ?? ?? E8` | FileRoot pointer (loaded-files table) |
| AreaChangeCounter | `FF 05 ^ ?? ?? ?? ?? 4D 8B 06` | files-loaded-in-area counter |
| Terrain Rotator Helper | `48 8D 05 ^ ?? ?? ?? ?? 4F 8D 04 40` | terrain height rotator array |
| Terrain Rotation Selector | `48 8D 0D ^ ?? ?? ?? ?? 44 0F B6 04 08` | terrain rotation selector array |
| GameCullSize | `2B 05 ^ ?? ?? ?? ?? 45 0F 57 C0` | map cull size |

## Top-level chain (`GameStateOffsets.cs`, `InGameStateOffset.cs`)

- **GameState** (from "Game States" pattern):
  - `+0x08` `CurrentStatePtr` — StdVector (current state)
  - `+0x48` `States` — inline array of 12 × `StdTuple2D<IntPtr>` (state slots; one is InGameState)
- **InGameState**:
  - `+0x290` `AreaInstanceData` → AreaInstance
  - `+0x310` `WorldData` → WorldData (area name + camera)
  - `+0x340` `UiRootStructPtr` → UiRootStruct
- **UiRootStruct**: `+0x5A8` `UiRootPtr` (self), `+0xBF0` `GameUiPtr`, `+0xBF8` `GameUiControllerPtr`

## AreaInstance (`AreaInstanceOffsets.cs`)

- `+0x0BC` `CurrentAreaLevel` (byte)
- `+0x0FC` `CurrentAreaHash` (uint)
- `+0x958` `Environments` (StdVector of `{ushort Key, ushort Value0, float Value1}`)
- `+0xA00` `PlayerInfo` (`LocalPlayerStruct`: `+0x00` ServerDataPtr, `+0x20` LocalPlayerPtr)
- `+0xB58` `Entities` (`EntityListStruct`: `AwakeEntities` StdMap, then `SleepingEntities` StdMap)
- `+0xD20` `TerrainMetadata` (`TerrainStruct`)
- `NETWORK_BUBBLE_RADIUS = 150` grid (conservative; real ~200, varies by entity type)
- Entity id filter: ignore visuals/decorations when `id < 0x40000000`

## Entities (`EntityOffsets.cs`) — **StdMap, not a linked list**

Entities live in two `StdMap`s (Awake + Sleeping), keyed by `EntityNodeKey {uint id; int pad}`,
value `EntityNodeValue {IntPtr EntityPtr}`. StdMap is a red-black tree (`StdMap.cs`:
`Head`/`Size`; node `Left/Parent/Right/Color/IsNil` then `Data{Key,Value}` at `+0x20`).

- **Entity**: `+0x00` ItemBase (`+0x08` EntityDetailsPtr, `+0x10` ComponentList StdVector),
  `+0x80` `Id` (uint), `+0x84` `IsValid` (byte; valid when bit0 clear)
- **EntityDetails**: `+0x08` `name` StdWString, `+0x28` `ComponentLookUpPtr`
- **ComponentLookUp**: `+0x28` `ComponentsNameAndIndex` (StdBucket).
  Entry `ComponentNameAndIndexStruct {IntPtr NamePtr; int Index}` → index into ComponentList.
- Friendly check: `(IsValid & 0x7F) == 0x01`

## Components

Each component starts with `ComponentHeader {+0x00 StaticPtr, +0x08 EntityPtr}`.

- **Life** (`Life.cs`): `+0x1A8` Health, `+0x1F8` Mana, `+0x230` EnergyShield — each a `VitalStruct`.
  - **VitalStruct**: `+0x10` ReservedFlat, `+0x14` ReservedPercent, `+0x28` Regen (float),
    `+0x2C` Total (int), `+0x30` Current (int). _(Same Total/Current layout as PoE1.)_
- **Positioned** (`Positioned.cs`): `+0x1E0` Reaction (byte). _(No GridPosition field here — see below.)_
- **Render** (`Render.cs`): `+0xB8` CurrentWorldPosition (`StdTuple3D<float>`),
  `+0xC4` ModelBounds (`StdTuple3D<float>`), `+0x130` TerrainHeight (float).
  - **Grid position is DERIVED**: `grid.X = world.X / WorldToGridRatio`, same for Y, where
    `WorldToGridRatio = 250f / 23 ≈ 10.87`. There is no Positioned.GridPosition read in PoE2.

## Camera / WorldToScreen (`WorldDataOffset.cs`)

- **WorldData**: `+0x98` WorldAreaDetailsPtr, `+0xA0` `CameraStructure`
- **CameraStructure**: `+0x100` `WorldToScreenMatrix` (Matrix4x4). _(Matrix is duplicated back-to-back.)_

## Terrain (`TerrainStruct` in `AreaInstanceOffsets.cs`)

- `+0x18` TotalTiles (`StdTuple2D<long>`)
- `+0x28` TileDetailsPtr (StdVector of `TileStructure`, 0x38 bytes each)
- `+0xD0` GridWalkableData (StdVector) — the walkable grid bytes
- `+0xE8` GridLandscapeData (StdVector)
- `+0x100` BytesPerRow (int) — for walkable/landscape data
- `+0x104` TileHeightMultiplier (short); `TileHeightFinalMultiplier = 7.8125f`
- **TileStructure** (0x38): `+0x00` SubTileDetailsPtr, `+0x08` TgtFilePtr, `+0x30` TileHeight,
  `+0x34` tileIdX, `+0x35` tileIdY, `+0x36` RotationSelector.
  `TileToGridConversion = 0x17` (23), `TileToWorldConversion = 250f`.
- **TgtFileStruct**: `+0x00` Vtable, `+0x08` TgtPath (StdWString).
- Subtiles are now variable-length (StdVector `SubTileHeight`), not the old fixed 23×23.

## UI elements (`UiElementBaseOffset.cs`, `MapUiElement.cs`, `ImportantUiElementsOffsets.cs`)

- **UiElementBase**: `+0x30` Self, `+0x38` Childs (StdVector), `+0x108` ParentPtr,
  `+0x110` RelativePosition (`StdTuple2D<float>`), `+0x12C` LocalScaleMultiplier,
  `+0x130` ScaleIndex (byte; root=3), `+0x140` StringId (StdWString), `+0x1B8` Flags (uint;
  visible = bit 0x0B, shouldModifyPos = bit 0x0A), `+0x240` UnscaledSize (`StdTuple2D<float>`),
  `+0x25C` BackgroundColor. BaseResolution = 2560×1600.
- **Map**: ImportantUiElements `+0x738` MapParentPtr (`+0xB40` for controller mode).
  - **MapParentStruct**: `+0x50` LargeMapPtr, `+0x58` MiniMapPtr
  - **MapUiElement**: `+0x2F8` Shift (`StdTuple2D<float>`), `+0x300` DefaultShift (default `(0,-20)`),
    `+0x338` Zoom (float)

## Native containers (`Natives/`)

`StdVector {IntPtr First; IntPtr Last; IntPtr End}` (count = (Last-First)/elemSize).
`StdMap {IntPtr Head; int Size}`; node: `Left/Parent/Right` ptrs, `Color`, `IsNil`, `Data{Key,Value}` @+0x20.
`StdWString` — SSO union (inline ≤8 chars else pointer) + length. `StdTuple2D/3D<T>` — packed X/Y(/Z).

---

## Structural deltas vs the current (PoE1-shaped) scaffold

These are the places the ported BubblesBot readers DO NOT match PoE2 and must be reshaped:

1. **Root chain.** Scaffold resolves IngameState via a single global pointer slot, then
   `IngameState.Data → IngameData`. PoE2 is `GameState → States[12] → InGameState → AreaInstanceData`.
   `Bootstrap` + `AobPatterns` + the IngameData/IngameState split need rework.
2. **Entity container.** Scaffold's `EntityListReader` walks a PoE1 linked list. PoE2 stores
   entities in two `StdMap` red-black trees (Awake + Sleeping). `EntityListReader` must be rewritten
   to walk StdMap nodes (filter `id < 0x40000000`, `IsValid` bit0).
3. **Grid position.** Scaffold reads `Positioned.GridPosition`. PoE2 derives grid from
   `Render.CurrentWorldPosition / 10.87`. `PlayerView`/`LivePlayer`/`EntityCache` change.
4. **Terrain.** Different struct + offsets; subtiles now variable-length. `TerrainGridReader` /
   `NavGrid` need remapping (walkable bytes at TerrainMetadata `+0xD0`, BytesPerRow `+0x100`).
5. **Camera.** Reachable via `WorldData(+0x310) → CameraStructure(+0xA0) → matrix(+0x100)`, not
   the PoE1 IngameState→Camera chain. `CameraView` rechain.
6. **Map UI.** `ImportantUiElements(+0x738) → MapParent → LargeMap`; Shift/DefaultShift/Zoom at
   new offsets. `MapView` rechain.
7. **Component map.** Resolved via `EntityDetails → ComponentLookUpPtr → StdBucket of (name,index)`
   indexing into the entity's ComponentList StdVector. Verify `EntityComponents.ReadComponentMap`
   matches this shape.
