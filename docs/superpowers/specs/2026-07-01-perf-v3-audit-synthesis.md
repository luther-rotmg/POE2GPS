# Perf v3 Stealth Reads — Audit Synthesis

## Baseline picture
Today's read profile in plain terms:

RENDER THREAD (~60 Hz, _liveRender) — dominates reads/sec when focused:
- Chain resolve (TryResolve): ~8 RPM/frame = ~480 RPM/sec, unconditional.
- AreaHash: 1 RPM/frame = ~60 RPM/sec, redundant (world snapshot already has it).
- PlayerGrid + PlayerWorld: 2 RPM/frame (same Render component read twice) = ~120 RPM/sec.
- ReadMap (2–4 elements): ~3 RPM/frame = ~180 RPM/sec.
- CameraMatrix: 2 RPM/frame = ~120 RPM/sec.
- PlayerVitals: 2–3 RPM/frame = ~150 RPM/sec.
- HP-bar live reads (TryLiveBarAt per mob): 2 RPM × N bar mobs × 60 Hz. With 20 visible bar mobs = ~2,400 RPM/sec.
- Item-label live position reads: 1 RPM × M items × 60 Hz (self-gated via empty list when GroundItems.Enabled=false).
- Atlas marks + route TryRelPos: 2 RPM × ~40 drawn marks/points × 60 Hz = ~4,800 RPM/sec while atlas is open.
Estimated render-thread total while focused and in a busy map: ~8,000–10,000 RPM/sec.

WORLD THREAD (~30 Hz, _live) — heavy but slower:
- Chain resolve (Probe): ~8 RPM/call = ~240 RPM/sec.
- Entity BFS (Entities): 1 RPM/node (48-byte batch) + 1 RPM position + conditional HP/mods/icon reads. In a 200-entity zone: ~600 RPM/tick from BFS traversal, plus ~200 position reads, plus ~200 HP reads (monsters+players), plus ReadMods uncached burst, plus ReadIcon per POI every tick. Rough entity walk total: ~1,200–2,000 RPM/tick = ~36,000–60,000 RPM/sec.
- Atlas ReadCanvasNodes (when atlas open): ~14 TryReadStruct/node × 1,100 nodes + iconType child-walk (~5 Ptr/node) + DataStorage reads (~3/node) = ~20,000 RPM/call × 30 Hz = ~600,000 RPM/sec while open and signature NOT frozen.
- PlayerLevel: 1 RPM/tick = 30 RPM/sec (massively oversampled).
- PlayerName: ~3 RPM/tick = 90 RPM/sec (never changes, never cached after first read).
- Monolith ReadMonolith: ~15 RPM/monolith/tick × 3 devices × 30 Hz = ~1,350 RPM/sec when Monoliths enabled.

SUMMARY OF DOMINANCE ORDER:
1. Atlas open + signature NOT frozen: ~600k RPM/sec (dominates everything else by 10×–100×).
2. Atlas open + signature frozen but ReadNodes still called: still ~600k RPM/sec (freeze-sig check is AFTER the full node walk).
3. Render-thread HP-bar live reads: ~2,400 RPM/sec per 20 bar mobs.
4. Render-thread atlas TryRelPos: ~4,800 RPM/sec while atlas open.
5. World-thread entity walk: ~36k–60k RPM/sec (BFS + position + HP + icons).
6. Everything else: individually small (PlayerVitals, CameraMatrix, ReadMap) but multiply at 60 Hz.

The single most impactful unopened opportunity is that ReadNodes runs its full 20k-read canvas walk every WorldTick even when the freeze-signature is immediately going to discard the result. The second-largest is the render-thread running all reads (chain resolve + vitals + camera + map) at full 60 Hz even when PoE2 is not the foreground window and the overlay draws nothing.

## Ranked reductions

### [1] Skip ReadCanvasNodes when atlas freeze-signature is unchanged (static-atlas fast path)
- **lever:** REDUNDANT-READ elimination / IDLE-SLOWDOWN
- **est reads saved:** ~20,000 RPM/call × 30 Hz = ~600,000 RPM/sec saved while atlas is open and view is static (the common in-use state). Even with a 10% 'changed' duty cycle, saves ~540,000 RPM/sec.
- **safety:** invisible
- **locations:** RadarApp.cs:2943–2957 (UpdateAtlas), Poe2Atlas.cs:380–429 (ReadCanvasNodes), Poe2Atlas.cs:339–375 (ReadNodes fast path)
- **functionality preservation:** The freeze-sig already exists precisely to detect when the view is static and inputs have not changed. Currently the sig is computed AFTER the full node walk (line 3007), so the walk's result is discarded when sig matches. Moving the gate BEFORE the walk leaves all user-visible behaviour identical: marks/route stay frozen, arrows don't jitter, zoom median is preserved from the last good read.
- **risk:** Low. The freeze-sig already drives mark/route freeze correctly. The only new risk is zoom-median staleness on the static path — mitigate by storing _atlasZoom from the last real ReadNodes call and publishing it unchanged on the skip path (already the behaviour since _atlasZoom is a field written inside UpdateAtlas, not inside ReadNodes).
- **implementation sketch:** In UpdateAtlas (RadarApp.cs ~line 2943): (1) Before calling _atlas.ReadNodes(), compute the lightweight pre-sig using the PREVIOUS frame's _atlasZoom + current input counts (startGrid, goalGrid, sel.Count, tag counts, AtlasAutoRoute flag, curGrid from _atlas.CurrentNodeGrid()). (2) If _builtAtlasOnce AND _atlas.AllTagsResolved AND pre-sig == _lastAtlasSig: skip ReadNodes entirely, skip the zoom-median loop, skip mark rebuild, and return immediately — the published _atlasRender stays frozen (correct). (3) Only call ReadNodes when pre-sig differs from _lastAtlasSig. Note: CurrentNodeGrid() is still called on the cheap path to contribute to the sig (4 RPM/tick → add its own slow-refresh per item 4 below rather than eliminating it here). The freeze-sig value stored in _lastAtlasSig is now set from the pre-sig (no change to sig computation logic, just moved before the node walk). AllTagsResolved gate already present — keep it.

### [2] Gate render-thread reads behind PoE2 foreground focus (unfocused idle-slowdown)
- **lever:** IDLE-SLOWDOWN
- **est reads saved:** All render-thread reads within the inGame block: ~8 base + 2N bar mobs + 2M atlas marks RPM/frame × 60 Hz, eliminated while PoE2 is not foreground. For a typical 20 bar mobs + 40 atlas points: ~(8+40+80) = ~128 RPM/frame = ~7,680 RPM/sec saved while tabbed out (which is a large fraction of session time for many users).
- **safety:** invisible
- **locations:** RadarApp.cs:1246–1355 (Tick, the `if (inGame)` block), RadarApp.cs:1517 (existing draw gate)
- **functionality preservation:** The overlay already skips Draw() when unfocused (line 1517). Auto-flask is foreground-gated by design. The HTTP API (/state) reads from the world snapshot, not the render thread. One transition frame (focus-loss) must still fire to clear the overlay. After that, skipping all inGame reads has no user-visible effect.
- **risk:** Low. The existing _overlayHadContent flag already implements the one-transition-frame pattern for the draw call — the same flag can gate the read block. TryResolve itself can still run at reduced rate (5 Hz) to maintain the inGame/areaHash for the /state API, but PlayerVitals, CameraMatrix, ReadMap, HP-bar reads, and atlas TryRelPos should all be skipped.
- **implementation sketch:** In Tick() (RadarApp.cs): wrap the `if (inGame)` block (lines 1263–1356) in `if (inGame && (realActive || _overlayHadContent))`. The TryResolve + AreaHash reads still run unconditionally (they are outside the inGame block in the current structure and are the minimum chain resolve). On the first unfocused frame: _overlayHadContent is still true so reads run once (one clear-frame). On subsequent unfocused frames: the guard is false so all reads in the block are skipped. The world thread continues at full rate (it serves the HTTP API and route maintenance). Optional: reduce world-thread WorldLoop cadence from 30 Hz to 10 Hz when !realActive by lengthening the sleep in WorldLoop — separate PR.

### [3] Gate ReadMods in Entities() on AffixNameplates.Enabled || DisplayRules.HasModFilter
- **lever:** FEATURE-GATE
- **est reads saved:** ReadMods is budget-capped at 16 new reads/tick. Each uncached monster mod read costs ~5–10 RPM (StdVector + affix array + Ptr per mod + ReadStringUtf16 per mod). First zone entry: up to 16×10 = 160 RPM/tick saved per tick until all monsters are cached, over ~N/16 ticks. Ongoing: 0 RPM (already cached), so the gate matters only at zone entry. With AffixNameplates off (most users): ~160 RPM/tick × ~(monster_count/16 ticks) eliminated per zone entry. Not a continuous saving but meaningful as a burst reduction.
- **safety:** invisible
- **locations:** Poe2Live.cs:526 (Entities loop, `var mods = cat == EntityCategory.Monster ? ReadMods(entity) : null`)
- **functionality preservation:** Mods are consumed only by AffixNameplates and DisplayRules with mod filters. When both are off the EntityDot.Mods field is never read by the renderer or BuildAffixSpecs(). The mod cache is per-entity so on re-enable the budget slowly re-fills — acceptable since enabling AffixNameplates is a deliberate user action, not a background event.
- **risk:** Low. Requires passing a bool into Entities() or exposing an EnableModReads property on Poe2Live. No change to the ReadMods implementation itself. ModCatalog.Observe() in RadarApp still iterates _entities and reads e.Mods — if mods are null (gate off) ModCatalog simply gets no data, which is correct when nameplates are off.
- **implementation sketch:** Add `public bool EnableModReads { get; set; }` to Poe2Live. In RadarApp.WorldTick(), before calling _live.Entities(areaInstance), set `_live.EnableModReads = _settings.AffixNameplates.Enabled || _displayRules.Any(r => r.HasModFilter)`. In Poe2Live.Entities() line 526: change `var mods = cat == EntityCategory.Monster ? ReadMods(entity) : null` to `var mods = (cat == EntityCategory.Monster && EnableModReads) ? ReadMods(entity) : null`. DisplayRules.HasModFilter is a bool property added to the display rule set that returns true when any enabled rule references a mod filter (a linear scan over the O(10) rules, cached per tick).

### [4] Gate ReadItemIdentity in Entities() on GroundItems.Enabled
- **lever:** FEATURE-GATE
- **est reads saved:** ReadItemIdentity is budget-capped at 12 new reads/tick. Each uncached item identity read costs ~8–12 RPM (WorldItem component resolve + Mods rarity/identified reads + RenderItem + art path string + Base + name string). First zone entry: up to 12×12 = 144 RPM/tick saved per tick until all drops are cached. When GroundItems is off (default for most users) ALL item identity reads are eliminated for the session.
- **safety:** invisible
- **locations:** Poe2Live.cs:532–533 (Entities loop, WorldItem identity branch)
- **functionality preservation:** BuildItemLabels() already early-returns empty when !cfg.Enabled (RadarApp line 1906). The only consumer of e.ItemArt and e.ItemName is BuildItemLabels(). Radar dots for WorldItem entities use e.Rarity for color — but rarity from ReadItemIdentity is only the ITEM rarity (used for color differentiation). When GroundItems is off, WorldItem dots can fall back to Rarity.NonMonster (no dot color loss since they would not be drawn or are drawn as generic dots anyway per DisplayRules).
- **risk:** Low-medium. Need to verify that WorldItem entity Rarity is not consumed by any other path (DisplayRules.Rarity, zone summary) when GroundItems is off. If it is, use a simpler gate: skip only the art path + name string reads but keep the rarity read. The rarity read itself is already cached per entity (ReadRarity path), so its marginal cost after first read is zero.
- **implementation sketch:** Add `public bool EnableItemIdentityReads { get; set; }` to Poe2Live. In RadarApp.WorldTick(), set `_live.EnableItemIdentityReads = _settings.GroundItems.Enabled || _displayRules.Any(r => r.AppliesToItems)`. In Poe2Live.Entities() line 532: change the WorldItem branch to `if (EnableItemIdentityReads && cat == EntityCategory.Other && meta.Contains("WorldItem", ...)) (rarity, itemArt, itemIdentified, itemName) = ReadItemIdentity(entity);` — else skip the call entirely (itemArt/itemName stay null, rarity stays NonMonster).

### [5] Eliminate redundant render-thread AreaHash RPM by reading from WorldSnapshot
- **lever:** REDUNDANT-READ elimination
- **est reads saved:** 1 RPM/frame × 60 Hz = 60 RPM/sec, unconditional while in-game and focused.
- **safety:** invisible
- **locations:** RadarApp.cs:1267 (`_areaHash = _liveRender.AreaHash(areaInstance)`), RadarApp.cs:1363 (snap.AreaHash already used for worldFresh check)
- **functionality preservation:** The world thread publishes snap.AreaHash (WorldSnapshot.AreaHash from _cachedAreaHash). The render thread already reads snap.AreaHash on line 1363 for the worldFresh zone-load guard. The only use of the separately-read _areaHash is for the API /state field and the same worldFresh check. Replacing the live read with snap.AreaHash introduces at most ~33 ms lag (one world tick) on zone entry — the zone-load guard still works correctly since the game takes multiple seconds to load.
- **risk:** Negligible. Zone transitions are seconds-long; 33 ms lag is undetectable. The snap reference is a volatile read already done at line 1257.
- **implementation sketch:** In Tick() (RadarApp.cs line 1267): delete `_areaHash = _liveRender.AreaHash(areaInstance);`. Replace all subsequent uses of `_areaHash` in the Tick() scope with `snap.AreaHash` (the snapshot was captured at line 1257, before the inGame block). The /state API field already comes from snap via the RadarState publish — no other change needed. Remove the AreaHash method call from the render-reader path.

### [6] Eliminate redundant PlayerGrid+PlayerWorld double-read of the same Render component
- **lever:** REDUNDANT-READ elimination
- **est reads saved:** 1 RPM/frame × 60 Hz = 60 RPM/sec, unconditional while in-game and focused.
- **safety:** invisible
- **locations:** RadarApp.cs:1269–1270, Poe2Live.cs:299/305 (PlayerGrid and PlayerWorld both call EntityWorld→TryReadStruct<Vector3>)
- **functionality preservation:** PlayerGrid converts PlayerWorld (a Vector3) to a 2D grid coordinate by dividing X/Y by WorldToGridRatio. Calling EntityWorld once and deriving both outputs saves the second identical RPM call with zero behavioural change.
- **risk:** Negligible. Pure refactor of two consecutive reads to one.
- **implementation sketch:** In Tick() (RadarApp.cs lines 1269–1270): call `playerWorld = _liveRender.PlayerWorld(localPlayer)` once. Derive `player = playerWorld is { } pv ? new NumVec2(pv.X / Poe2.WorldToGridRatio, pv.Y / Poe2.WorldToGridRatio) : NumVec2.Zero`. Delete the separate PlayerGrid() call. Add a PlayerGrid(Vector3 world) overload in Poe2Live that takes the already-read world vector and skips the RPM call, or just inline the division.

### [7] Gate CameraMatrix read behind world-space feature flags
- **lever:** FEATURE-GATE
- **est reads saved:** 2 RPM/frame × 60 Hz = 120 RPM/sec when all world-space overlays are off.
- **safety:** invisible
- **locations:** RadarApp.cs:1272 (`_cameraMatrix = _liveRender.CameraMatrix(inGameState)`)
- **functionality preservation:** CameraMatrix is consumed only by DrawNameplates/DrawItemLabels/DrawAffixNameplates in OverlayRenderer. If HpBars all off AND AffixNameplates.Enabled=false AND GroundItems.Enabled=false, the matrix is read and stored but never used. Setting _cameraMatrix to null when unused causes the renderer to skip world-space draws (it already null-checks).
- **risk:** Low. A clear boolean gate on four well-defined settings flags.
- **implementation sketch:** In Tick() (RadarApp.cs line 1272): wrap with `if (_settings.HpBarNormal || _settings.HpBarMagic || _settings.HpBarRare || _settings.HpBarUnique || _settings.AffixNameplates.Enabled || _settings.GroundItems.Enabled) { _cameraMatrix = _liveRender.CameraMatrix(inGameState); } else { _cameraMatrix = null; }`. No renderer change needed — OverlayRenderer already null-checks _cameraMatrix before world-space draws.

### [8] Gate PlayerVitals render-thread read behind auto-flask and HUD features
- **lever:** FEATURE-GATE
- **est reads saved:** 2–3 RPM/frame × 60 Hz = 120–180 RPM/sec when both auto-flask and HUD vitals display are off.
- **safety:** invisible
- **locations:** RadarApp.cs:1273 (`if (_liveRender.PlayerVitals(localPlayer) is { } v) ...`)
- **functionality preservation:** PlayerVitals feeds auto-flask (safety-critical, must stay at render rate when enabled) and the HUD %HP/%Mana/%ES display. When both consumers are off the read produces values never shown to the user. When auto-flask is off but HUD is on: a slow-refresh to ~10 Hz is imperceptible for a percentage indicator (human perception of a bar changing is ~100 ms, far above 100 ms cadence).
- **risk:** Low. The auto-flask gate is a single EnableAutoFlask flag check. Slow-refresh for HUD-only mode adds a counter field.
- **implementation sketch:** In Tick() (RadarApp.cs line 1273): `var needVitals = _settings.EnableAutoFlask || _settings.ShowVitalsHud;` If `_settings.EnableAutoFlask`: read at full render rate (current behaviour). Else if `_settings.ShowVitalsHud && _vitalRefreshCounter++ % 6 == 0`: read at ~10 Hz (every 6 render frames at 60 fps). Else: skip entirely, leave _hpPct/_manaPct/_esPct at their last known values (stale but invisible to the user since both displays are off).

### [9] Gate atlas iconType child-walk on AtlasShowContentIcons setting
- **lever:** FEATURE-GATE
- **est reads saved:** Up to 10 RPM/node × 1,100 nodes × 30 Hz = ~330,000 RPM/sec when AtlasShowContentIcons=false and atlas is open (only applies when ReadCanvasNodes actually runs — combined with rank-1 reduction, savings are on the fraction of ticks where the sig changed).
- **safety:** invisible
- **locations:** Poe2Atlas.cs:412–416 (ReadCanvasNodes iconType loop)
- **functionality preservation:** iconType is used only for content-icon rendering on node rings (the sigil graphic inside each node circle). When AtlasShowContentIcons is false no icon is drawn regardless. The iconType also feeds the freeze-signature (line 3006) — but that line already reads `_settings.AtlasShowContentIcons ? 1L : 0L` as a toggle signal, meaning when the setting is off the child-walk result is unused anyway.
- **risk:** Negligible. Wrapping lines 412–416 in a single if-check. Already included in freeze-sig so the system correctly rebuilds when the setting is toggled.
- **implementation sketch:** In Poe2Atlas.ReadCanvasNodes() (lines 412–416): wrap the iconType for-loop in `if (_settings.AtlasShowContentIcons) { var d = el; for (var lvl = 0; ...) { ... } }`. The iconType variable stays declared at 0 (its default, meaning no icon). Requires passing RadarSettings (or the bool) into Poe2Atlas, which already receives settings for AllTagsResolved. Alternatively expose an AtlasShowContentIcons property on Poe2Atlas set by RadarApp before each UpdateAtlas call.

### [10] Gate atlas DataStorage/DataModel/DataStatus reads on AtlasAutoRoute || AtlasHideCompleted || AtlasHideAccessible
- **lever:** FEATURE-GATE
- **est reads saved:** 3 RPM/node × 1,100 nodes × 30 Hz = ~99,000 RPM/sec when all three settings are off and atlas is open (again applies on the ticks ReadCanvasNodes runs).
- **safety:** invisible
- **locations:** Poe2Atlas.cs:421–427 (ReadCanvasNodes accessible/completed reads)
- **functionality preservation:** accessible and completed flags are consumed only by auto-route source frontier, AtlasHideCompleted filter, and AtlasHideAccessible filter. When all three are off these booleans are assigned but never used in mark-building or route computation.
- **risk:** Low. Simple boolean gate. The three consuming settings are already included in the freeze-signature so the rebuild fires correctly when they are toggled on.
- **implementation sketch:** In Poe2Atlas.ReadCanvasNodes() before line 421: `bool needStatus = _settings.AtlasAutoRoute || _settings.AtlasHideCompleted || _settings.AtlasHideAccessible;`. Wrap lines 421–427 in `if (needStatus) { var storage = Ptr(el + ...); ... }`. When not needed, accessible and completed stay false (their default), which is the correct safe default (treat all nodes as neither accessible nor completed — no marks are incorrectly hidden).

### [11] Apply one-way completion cache to ReadIcon (POI CompletedState)
- **lever:** SLOW-REFRESH / one-way cache
- **est reads saved:** 1 RPM/POI entity/tick × (number of POIs) × 30 Hz. In a typical map with 5–15 POI entities: ~150–450 RPM/sec eliminated for already-completed POIs. For not-yet-completed POIs: slow-refresh to every 10 ticks (~3 Hz) saves ~270 ms check interval vs 33 ms — ~10× reduction in RPM for live POIs.
- **safety:** imperceptible
- **locations:** Poe2Live.cs:560–569 (ReadIcon), Poe2Live.cs:535 (Entities loop call site)
- **functionality preservation:** CompletedState is a one-way transition (0 → non-zero, never reverses within a zone). Once confirmed completed, the value cannot revert. The one-way cache exactly mirrors ReadChestOpened which already uses this pattern. For not-yet-completed POIs, 10-tick polling (~333 ms) catches the completion event well within human perception (the player must physically interact for at least 1–2 seconds).
- **risk:** Low. Identical pattern to ReadChestOpened (_openedChests HashSet), already proven in production.
- **implementation sketch:** Add `private readonly HashSet<nint> _completedPois = new();` to Poe2Live (cleared in the areaInstance cache-clear block alongside _openedChests, and in EvictEntity). Add a `_poiCheckTick` counter (int, per-entity, stored in a Dictionary<nint,int> _iconCheckAt). In ReadIcon(): if `_completedPois.Contains(icon)` return (true, true) with 0 RPM. Else if `_iconCheckAt.TryGetValue(icon, out var lastTick) && (_reactionTick - lastTick) < 10` return last known value. Else: read CompletedState (1 RPM), update _iconCheckAt[icon] = _reactionTick, if complete add to _completedPois. This mirrors the reaction slow-refresh pattern already present in the codebase.

### [12] Cache PlayerName after first successful read (eliminate per-tick ReadStdWString)
- **lever:** SLOW-REFRESH / permanent cache
- **est reads saved:** 2–3 RPM/tick × 30 Hz = 60–90 RPM/sec. Small in absolute terms but zero-risk.
- **safety:** invisible
- **locations:** Poe2Live.cs:285–288 (PlayerName), RadarApp.cs:~1560 (WorldTick caller)
- **functionality preservation:** Player name never changes within a session. The component address is already cached (_plPlayerFor). Only the ReadStdWString fires each tick. Cache the returned string after first non-empty read and return the cached string thereafter. On zone change the localPlayer address may change but the name stays the same character — the cache is valid for the entire session.
- **risk:** Negligible. A simple string field `_cachedPlayerName` checked before the ReadStdWString call.
- **implementation sketch:** In Poe2Live.PlayerName(): add `private string? _cachedPlayerName;`. At the top of the method: `if (_cachedPlayerName != null) return _cachedPlayerName;`. After the ReadStdWString call: `if (!string.IsNullOrEmpty(result)) _cachedPlayerName = result;`. No cache invalidation needed (name never changes; the overlay is restarted between characters).

### [13] Slow-refresh PlayerLevel from 30 Hz to once per 5 seconds
- **lever:** SLOW-REFRESH
- **est reads saved:** ~29/30 of 1 RPM/tick × 30 Hz = ~29 RPM/sec. Small but zero-risk.
- **safety:** imperceptible
- **locations:** RadarApp.cs:1560 (`_charLevel = _live.PlayerLevel(localPlayer)`)
- **functionality preservation:** Character level is displayed in the HUD and session tracker. It changes at most once per minute during active leveling, never during endgame mapping. A 5-second refresh (150 ticks) is imperceptible for a level indicator.
- **risk:** Negligible. A simple tick counter gate.
- **implementation sketch:** In RadarApp.WorldTick() line 1560: add `if (_levelRefreshCounter++ % 150 == 0) _charLevel = _live.PlayerLevel(localPlayer);`. The field _charLevel retains its last known value between refreshes. PlayerLevel() already caches the component address (_plPlayerFor) so the RPM is only the byte read itself.

### [14] Gate Player HP reads in Entities() — remove EntityCategory.Player from ReadHp condition
- **lever:** FEATURE-GATE
- **est reads saved:** 1 RPM/Player entity/tick × (party size 1–6) × 30 Hz = ~30–180 RPM/sec.
- **safety:** invisible
- **locations:** Poe2Live.cs:523 (`if (cat is EntityCategory.Monster or EntityCategory.Player)`)
- **functionality preservation:** Player vitals for auto-flask and the HUD are read by the render thread (_liveRender.PlayerVitals) independently. The EntityDot.HpCur/HpMax fields for Player entities are not consumed by any world-rate feature (HP bars only show monsters; zone summary counts monsters not players). Changing the condition to `cat == EntityCategory.Monster` only eliminates reads whose results go unused.
- **risk:** Low. Verify no feature reads HpCur/HpMax from Player EntityDots (grep for EntityDot and Player+HP shows only the display-filter path which gates on category == Monster anyway).
- **implementation sketch:** In Poe2Live.Entities() line 523: change `if (cat is EntityCategory.Monster or EntityCategory.Player) (hpCur, hpMax) = ReadHp(entity);` to `if (cat == EntityCategory.Monster) (hpCur, hpMax) = ReadHp(entity);`. One-line change.

### [15] Slow-refresh CurrentNodeGrid in UpdateAtlas to ~1 Hz
- **lever:** SLOW-REFRESH
- **est reads saved:** ~3.9 RPM/tick × 30 Hz × (29/30 refresh skip) = ~117 RPM/sec while atlas is open. Note: with rank-1 in place this only matters on the ticks ReadCanvasNodes actually runs (sig changed), so the combined saving is smaller.
- **safety:** imperceptible
- **locations:** Poe2Atlas.cs:461–472 (CurrentNodeGrid), RadarApp.cs:2972 (caller in UpdateAtlas)
- **functionality preservation:** CurrentNodeGrid changes only when the player exits a completed map and enters a new one — at most once every few minutes. A 1 Hz read (every 30 world ticks) adds at most ~1 second of lag before the auto-route source updates. The re-solve itself happens at world tick rate so even with a 1-second stale current-node, the route re-solves within one tick of the refresh.
- **risk:** Low. Cached between refreshes with last known value. Imperceptible because map transitions take several seconds.
- **implementation sketch:** In RadarApp (or Poe2Atlas directly): add `private int _curGridRefreshCounter;` and `private (int X, int Y)? _cachedCurGrid;`. In UpdateAtlas: `if (_curGridRefreshCounter++ % 30 == 0) _cachedCurGrid = _atlas.CurrentNodeGrid();` and use `_cachedCurGrid` as curGrid in the sig computation. Keeps the sig stable during the 29 skipped ticks (correct — current node hasn't changed).

### [16] Batch ReadMonolith — cache static station data, live-read only Collected flag
- **lever:** SLOW-REFRESH / partial cache
- **est reads saved:** ~14 RPM per monolith static chain × 3 devices × 30 Hz = ~1,260 RPM/sec saved (down to ~3 RPM/device/tick for the Collected flag only). When Monoliths.Enabled=false: already 0 (gated). Enabled case is the target.
- **safety:** invisible
- **locations:** Poe2Live.cs:583–620 (ReadMonolith), RadarApp.cs:1690 (UpdateMonoliths caller)
- **functionality preservation:** HoleCount, AnchorIdx, AnchorPos, IsUnique are set at device placement and never change within an area. Only Collected (= MinimapIcon.CompletedState) can flip when the player claims a reward. Caching the static fields per device address and re-reading only the Collected flag matches the ReadChestOpened / ReadIcon pattern exactly.
- **risk:** Low-medium. The station chain (StateMachine → ListenerVec → station) must be walked once to find the station address, then cached. If the entity evicts the cache must be cleared (already handled by EvictEntity pattern). The Collected flag uses ReadIcon which is getting its own one-way cache (rank-11) — combine the two for free.
- **implementation sketch:** Add `private Dictionary<nint, MonolithState> _monolithCache = new();` to Poe2Live (cleared on areaInstance change). In ReadMonolith(): if `_monolithCache.TryGetValue(device, out var cached)`: re-read only Collected via ReadIcon (which will use its one-way cache from rank-11), return `cached with { Collected = collected }`. On cache miss: run full chain walk, store result minus Collected in cache, return full result with live Collected. In EvictEntity(): add _monolithCache.Remove(entity).

### [17] Cull off-screen atlas marks from render-thread TryRelPos loop
- **lever:** CULL-BEFORE-READ
- **est reads saved:** ~50% of atlas mark TryRelPos reads eliminated (off-screen marks). With ~100 total marks and ~50% off-screen: 2 RPM × 50 marks × 60 Hz = ~6,000 RPM/sec saved while atlas is open.
- **safety:** invisible
- **locations:** RadarApp.cs:1319–1354 (Tick, atlas marks TryRelPos loop, `foreach (var m in ar.Marks)`)
- **functionality preservation:** Off-screen marks are never drawn by the renderer. Skipping the TryRelPos read for marks whose baked position (Bx, By) is outside the window bounds (with generous margin ~200px) means they fall back to the baked position in _atlasMarkFrame — but since they are not drawn, the fallback is never used. On-screen marks still get the fresh relPos read.
- **risk:** Low. The baked position (set at world rate in UpdateAtlas) is the correct culling reference. A mark that pans from off-screen to on-screen within one render frame will use the baked position for that frame, then the live position in the next — no visual artifact since it was off-screen the previous frame.
- **implementation sketch:** In the Tick() marks loop (RadarApp.cs line 1322): before calling TryRelPos, check if the mark's baked position projects onto screen: `float pscale = (_window.Height / 1600f) * _atlasZoom; float bsx = m.Bx * pscale, bsy = m.By * pscale; bool onScreen = bsx > -200 && bsx < _window.Width + 200 && bsy > -200 && bsy < _window.Height + 200;`. If !onScreen: append `m` directly to _atlasMarkFrame (baked position) without calling TryRelPos. If onScreen: call TryRelPos as today.

## Proposed task breakdown

1. Task SR-1 (Invisible, Highest Impact): Static-atlas fast path — skip ReadCanvasNodes when freeze-signature unchanged. In RadarApp.UpdateAtlas(), compute the pre-sig from _atlasZoom + input counts BEFORE calling _atlas.ReadNodes(). If _builtAtlasOnce AND AllTagsResolved AND pre-sig == _lastAtlasSig: return immediately without calling ReadNodes. Saves ~600k RPM/sec while atlas is open and static. Deliverable: verified by /state rpmPerSec dropping dramatically when the atlas panel is open and the user is not panning.
2. Task SR-2 (Invisible, High Impact): Unfocused idle-slowdown — gate all render-thread reads behind realActive || _overlayHadContent. In Tick(), wrap the `if (inGame)` block (lines 1263–1355) so the entire read block is skipped on frames where PoE2 is not the foreground window AND the one-frame clear has already fired. TryResolve still runs (outside the block) to maintain inGame state for the API. Deliverable: verified by rpmPerSec dropping to near world-thread baseline when alt-tabbed.
3. Task SR-3 (Invisible, Medium Impact): Feature-gate ReadMods in Entities() on AffixNameplates.Enabled || HasModFilter. Add EnableModReads property to Poe2Live; set it in RadarApp.WorldTick() before calling Entities(). Change line 526 to check EnableModReads. Deliverable: verified by rpmPerSec at zone entry being lower when AffixNameplates is off (compare /state rpmPerSec across two zone entries with the flag toggled).
4. Task SR-4 (Invisible, Medium Impact): Feature-gate ReadItemIdentity in Entities() on GroundItems.Enabled. Add EnableItemIdentityReads property to Poe2Live; set in WorldTick(). Change the WorldItem branch to skip ReadItemIdentity when the gate is false. Deliverable: same verification pattern as SR-3; rpmPerSec at zone entry lower with GroundItems off.
5. Task SR-5 (Invisible, Low-Risk Quick Wins — batch into one PR): (a) Eliminate render-thread AreaHash RPM: replace _liveRender.AreaHash() call with snap.AreaHash (1 line deletion). (b) Eliminate redundant PlayerGrid+PlayerWorld double-read: call PlayerWorld once, derive grid inline (refactor 2 lines). (c) Gate CameraMatrix read behind HpBar/AffixNameplates/GroundItems flags (3-line if-wrap). (d) Remove EntityCategory.Player from ReadHp condition in Entities() (1-char change). Deliverable: all four are one-line-to-three-line changes; bundle into single commit; verify rpmPerSec drops by ~200–300 RPM/sec in baseline focused state.
6. Task SR-6 (Invisible/Imperceptible, Low-Risk Quick Wins — batch into one PR): (a) Cache PlayerName after first non-empty read in Poe2Live (add _cachedPlayerName field, check before ReadStdWString). (b) Slow-refresh PlayerLevel to every 150 ticks (add _levelRefreshCounter in RadarApp). (c) Gate PlayerVitals behind EnableAutoFlask || ShowVitalsHud with 10 Hz slow-refresh for HUD-only mode (add _vitalRefreshCounter). Deliverable: negligible individual savings but establishes the slow-refresh pattern for future extensions.
7. Task SR-7 (Invisible, Medium Impact): Atlas content-icon and accessible/completed feature gates. In Poe2Atlas.ReadCanvasNodes(): (a) wrap iconType loop in `if (showContentIcons)` (saves ~10 RPM/node). (b) wrap DataStorage/DataModel/DataStatus reads in `if (needStatus)` gate (saves ~3 RPM/node). Requires passing settings flags into Poe2Atlas (add two bool properties set before ReadCanvasNodes). Deliverable: verified by rpmPerSec delta while atlas open with AtlasShowContentIcons=false and AtlasAutoRoute=false.
8. Task SR-8 (Invisible, Medium Impact): POI CompletedState one-way cache in ReadIcon. Add _completedPois HashSet and _iconCheckAt Dictionary<nint,int> to Poe2Live. Implement one-way cache (once complete, never re-read) + 10-tick slow-refresh for not-yet-completed POIs. Clear both in zone-change block and EvictEntity. Deliverable: small continuous RPM reduction; verified by inspecting ReadCount delta across a zone with multiple expedition/shrine POIs.
9. Task SR-9 (Invisible, Low Impact): Batch ReadMonolith static-data cache. Add _monolithCache Dictionary<nint,MonolithState> to Poe2Live. On cache hit: return cached with re-read Collected only (using rank-8 ReadIcon one-way cache — do after SR-8). Implement full chain walk only on cache miss. Clear on zone change and EvictEntity. Deliverable: ~1,260 RPM/sec saved when Monoliths.Enabled=true.
10. Task SR-10 (Invisible, Medium Impact): Atlas off-screen mark cull before TryRelPos. In Tick() atlas marks loop, compute baked screen position from m.Bx/m.By using _atlasZoom and window size; skip TryRelPos for marks outside bounds + generous margin (200px). Deliverable: ~6,000 RPM/sec saved while atlas is open; verified by rpmPerSec drop when atlas panel is open with many off-screen marks.
11. Task SR-11 (Imperceptible, Low Impact): CurrentNodeGrid slow-refresh to ~1 Hz. Add _curGridRefreshCounter and _cachedCurGrid to RadarApp; refresh only every 30 world ticks. Use cached value in UpdateAtlas sig computation. Do AFTER SR-1 since SR-1 reduces how often UpdateAtlas's sig path runs anyway.

## Summary
The v0.18.0 Stealth Reads plan targets 11 independently-testable deliverables in priority order. The single biggest win (SR-1) is moving the atlas freeze-signature check BEFORE the ReadCanvasNodes node walk rather than after — this eliminates up to 600,000 RPM/sec while the atlas is open and static, which is the dominant read source by an order of magnitude over everything else combined. The second biggest win (SR-2) gates all render-thread reads behind PoE2 foreground focus, eliminating ~8,000–10,000 RPM/sec while alt-tabbed or minimized. Together SR-1 and SR-2 likely reduce average reads/sec by 60–80% for a typical mapping session (atlas open ~20% of the time; unfocused ~30% of the time). The remaining tasks (SR-3 through SR-11) are individually modest (30–6,000 RPM/sec each) but all zero-risk invisible or imperceptible changes. Every reduction passes the guardrail: no on-screen dynamic data drops below 30 Hz, auto-flask stays at render rate, and no user-visible feature is degraded. Implementation is staged cheapest-first: quick-win batches (SR-5, SR-6) before the more complex caching changes (SR-8, SR-9).