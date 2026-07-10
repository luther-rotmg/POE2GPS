using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// A unified navigation target — either a static terrain-tile landmark or an entity POI — addressed
/// by a STABLE STRING id so a selection survives world ticks and (where it matches) re-applies across
/// zones. <see cref="Id"/> is "t:&lt;path&gt;" for tiles, "e:&lt;entityId&gt;" for entities;
/// <see cref="MatchKey"/> (landmark path or entity metadata) is what auto-nav patterns match against;
/// <see cref="Grid"/> is the A* goal cell.
/// </summary>
public readonly record struct NavTarget(string Id, string Name, NumVec2 Grid, string MatchKey, bool IsEntity, bool AutoPath = false);

/// <summary>One entry in the priority-then-distance ranked target list the cycler walks.</summary>
public readonly record struct RankedTarget(string Id, string Name, string Category);

/// <summary>Transient on-screen "active target" indicator state (drawn briefly after a cycle).</summary>
public sealed record CycleIndicator(int Pos, int Total, string Name, string Category, System.DateTime Expiry);

/// <summary>One legend row: a navigation target, the selection-order color slot it draws in (0..7, or
/// -1 when unselected), and whether it is currently selected (its own A* route is drawn).</summary>
public readonly record struct LegendEntry(NavTarget Target, int ColorSlot, bool IsSelected);

/// <summary>One selected target's smoothed A* route: the selection-order color slot (0..7) used to pick
/// its draw/legend color and the smoothed grid-cell waypoints. Empty <see cref="Points"/> = no path.</summary>
public readonly record struct SelectedPath(int ColorSlot, IReadOnlyList<(int x, int y)> Points);

/// <summary>A monster HP bar to draw, with everything expensive already decided at world rate: the style
/// (width + packed 0xAARRGGBB fill/border colors) was resolved once when the entity set was built; only
/// <see cref="World"/> + <see cref="Frac"/> are refreshed live every render frame (cheap per-entity reads)
/// so the bar tracks the moving monster smoothly. The renderer just projects + fills.</summary>
public readonly record struct HpBarTarget(Vector3 World, float Frac, float Width, uint Fill, float BorderWidth, uint Border);

/// <summary>One mob whose affix nameplate should be drawn this frame. <see cref="World"/> is the mob's
/// live world position (re-read from its Render component every render frame via
/// <c>_liveRender.TryLiveBarAt</c>, exactly like <see cref="HpBarTarget.World"/>); <see cref="Lines"/>
/// is the pre-filtered, pre-formatted list built at world rate by <c>BuildAffixSpecs</c>. The renderer
/// projects <see cref="World"/> and draws the lines above the HP bar. Null/empty list → skip.</summary>
public readonly record struct AffixNameplateTarget(Vector3 World, POE2Radar.Core.Game.AffixLine[] Lines);

/// <summary>One mob whose buff tags should be drawn this frame. World = live position (re-read each frame);
/// Lines = pre-filtered buff labels (with timers) from BuildBuffSpecs. Drawn BELOW the mob.</summary>
public readonly record struct BuffNameplateTarget(Vector3 World, POE2Radar.Core.Game.BuffLine[] Lines);

/// <summary>A priced ground-item label drawn over the in-world loot icon. <see cref="World"/> is the
/// dropped item's world position (projected via the camera matrix, like HP bars). <see cref="Name"/> is
/// the resolved unique name (from the art→price map — shown even for UNIDENTIFIED items), <see cref="Value"/>
/// the formatted price, and <see cref="Highlight"/> whether it's above the configured value threshold
/// (→ border). Built at world rate in RadarApp; the renderer only projects + draws.</summary>
/// <summary><see cref="ShowName"/> = draw the resolved item NAME above the value (with a backing panel) —
/// used for UNIDENTIFIED uniques, whose name the game hides. When false (identified uniques, runes,
/// essences, currency…) only the value is drawn in a compact chip. <see cref="Highlight"/> adds a border.</summary>
public readonly record struct ItemLabel(Vector3 World, string Name, string Value, bool Highlight, bool ShowName);

/// <summary>One atlas node to highlight. <see cref="X"/>/<see cref="Y"/> are the canvas-space CENTER of
/// the node (RelativePos top-left + half node dimension), via <c>AtlasGeometry.AtlasCentre</c>; the
/// renderer projects them to screen via the atlas transform (scale + offset).</summary>
/// <summary><see cref="Element"/> is the node's UiElement address; the render thread re-reads its
/// RelativePos into <see cref="X"/>/<see cref="Y"/> every frame so rings track atlas pan smoothly (the
/// X/Y published by the world walk are the last-known fallback). 0 = no live element.</summary>
public readonly record struct AtlasMark(
    float X, float Y, float W, float H,
    bool Selected, bool HasContent, bool Visited, bool Unlocked,
    int Biome, int IconType,
    string? Label = null, string? Color = null,
    bool Arrow = false, bool Nav = false, nint Element = 0,
    IReadOnlyList<string>? ContentIcons = null, bool Visible = false,
    int GridX = 0, int GridY = 0);

/// <summary>One auto-route polyline from the player's current atlas node (or the accessible frontier) to a
/// tracked target tile. <see cref="Points"/> are canvas-space node centers (relPos), projected with the
/// same atlas transform as the marks; <see cref="Color"/> is the target rule's ring colour ("#RRGGBB" or
/// null → default); <see cref="Hops"/> is the number of map steps (drawn as a chip at the target).</summary>
public readonly record struct AtlasRouteInfo(IReadOnlyList<NumVec2> Points, string? Color, int Hops);

/// <summary>One priced reward row in the "Runeshape Combinations" panel. <see cref="X"/>/<see cref="Y"/>/
/// <see cref="W"/>/<see cref="H"/> are the reward row's SCREEN rect (already scaled, from Poe2Runeforge);
/// the renderer draws <see cref="Text"/> (e.g. "5.4 ex") in <see cref="Color"/> (packed 0xAARRGGBB) just
/// outside the row's right edge. Built at world rate in RadarApp; the renderer only positions + draws.</summary>
public readonly record struct RuneLabel(float X, float Y, float W, float H, string Text, uint Color);

/// <summary>A priced reward in the post-ritual TRIBUTE SHOP. <see cref="X"/>/<see cref="Y"/>/<see cref="W"/>/
/// <see cref="H"/> are the reward tile's SCREEN rect (already scaled, from <c>Poe2Live.ReadRitualRewards</c>);
/// the renderer draws <see cref="Text"/> (e.g. "12 ex") in <see cref="Color"/> (packed 0xAARRGGBB) on the
/// tile, with a border when <see cref="Highlight"/>. Built at world rate in RadarApp; the renderer positions
/// + draws. Drawn whenever the tribute shop is open.</summary>
public readonly record struct RitualLabel(float X, float Y, float W, float H, string Text, uint Color, bool Highlight);

/// <summary>One reward a runeshape monolith will offer (computed BEFORE the panel is opened, from the
/// device→station read + offline catalog). <see cref="Ex"/> is the priced full-stack value in Exalted
/// (0 = unpriced); <see cref="Runes"/> is the rune pattern (for tooltips/detail).</summary>
public readonly record struct MonolithReward(string Name, int Count, double Ex, int Size, string Runes);

/// <summary>A runeshape monolith to mark on the map. <see cref="Grid"/> is the device's grid position
/// (projected like an entity dot / landmark); <see cref="Holes"/> is N (the slot count, drawn as a badge);
/// <see cref="BestEx"/>/<see cref="BestName"/> is its most valuable offered reward; <see cref="Color"/>
/// (packed 0xAARRGGBB) is the value tier. <see cref="Rewards"/> is the full priced offer set (panel +
/// dashboard), value-sorted. Built at world rate in RadarApp; the renderer projects + draws.</summary>
public sealed record MonolithMarker(
    NumVec2 Grid, int Holes, bool IsUnique, bool Collected, string AnchorName,
    double BestEx, string BestName, uint Color, IReadOnlyList<MonolithReward> Rewards);

/// <summary>One preload-alert entry surfaced on zone entry: a catalog match that passed the frequency
/// filter and met the configured MinTier. Built once per zone change (never rebuilt per tick); the
/// render thread reads it from the WorldSnapshot via the zone-load guard.</summary>
// Signal — SIG-PRELOAD-CATALOG (v0.23): SpawnEntityMetadata is the substring Task 5 (SIG-PRELOAD-
// HIDE-ON-SPAWN) scans _entities for; when found, the WorldTick pass sets Spawned = true via record
// `with` expression and the renderer skips the row. Null SpawnEntityMetadata = opt-out (Shrines,
// Chests, Rituals — tile-scoped content where a spawned entity does not mean "encounter resolved").
public readonly record struct PreloadHit(string Label, string Tier, string Category, string Color, string? SpawnEntityMetadata, bool Spawned);

/// <summary>One off-screen entity arrow to draw this frame. <see cref="World"/> is the entity's live
/// world position (re-read from its Render component every render frame via
/// <c>_liveRender.TryLiveBarAt</c>, exactly like <see cref="HpBarTarget.World"/>); <see cref="Color"/>
/// is the packed 0xAARRGGBB fill from the matching DisplayRule; <see cref="Label"/> is the optional
/// rule name drawn beside the arrowhead. The renderer projects <see cref="World"/> via the camera matrix,
/// determines the off-screen direction, and draws an edge arrow. Null label → no text.</summary>
public readonly record struct EntityArrowTarget(Vector3 World, uint Color, string? Label);

/// <summary>Live zone-aggregate counts published by the world thread and gated on AreaHash in Tick().</summary>
public readonly record struct ZoneSummary(
    int MonstersAlive,
    int RareEliteAlive,
    int ChestsOpen,
    int ChestsClosed,
    int Transitions,
    int Landmarks,
    int ExpeditionCount,
    int RitualCount,
    int BreachCount,
    int StrongboxCount,
    int EssenceCount,
    int ShrineCount,
    // Chorus — CHOR-23 (v0.25): three new chips computed in the same entity walk that fills the
    // mechanic counts above. Rendered by DrawZoneSummary in OverlayRenderer.
    int KillsThisZone = 0,
    float NearestMechanicDist = 0f,
    string? NearestMechanicKind = null,
    bool HasBossArena = false);

/// <summary>What the PoE2 renderer needs each frame. Built fresh by <see cref="RadarApp"/>.</summary>
public sealed record RenderContext(
    bool InGame,
    bool Active,            // PoE2 is the foreground window — draw nothing when false
    int WindowWidth,
    int WindowHeight,
    NumVec2 PlayerGrid,
    // Live (render-rate) player world position incl. Z; anchors the world-ground guidance line at the
    // player's feet. Null when unavailable (not in game / read failed). NOT from the entity list (the
    // local player is filtered out of that), so it's correct even when alive/dead state changes.
    Vector3? PlayerWorld,
    Poe2Live.MapUi Map,
    IReadOnlyList<Poe2Live.EntityDot> Entities,
    IReadOnlyList<Poe2Live.Landmark> Landmarks,
    uint AreaHash,
    Poe2Live.TerrainData? Terrain,
    // Live projection calibration (adjustable at runtime).
    float ScaleMul,
    float OffsetX,
    float OffsetY,
    // Player vitals (read-only HUD).
    float HpPct,
    float ManaPct,
    float EsPct,
    // Area / character HUD.
    string AreaCode,
    int CharLevel,
    // WorldToScreen matrix (16 floats, row-major) for world-space nameplates; null if unavailable.
    float[]? CameraMatrix,
    // Transient on-screen indicator shown briefly after a Quick-Target cycle; null when not active/expired.
    CycleIndicator? CycleIndicator,
    // ── Phase 1 features (all gated by their settings flag below). ──
    // Feature flags mirrored from RadarSettings.
    bool HideJunk,
    bool ShowPath,
    bool UseCuratedLandmarks,
    // Radar display toggles.
    bool ShowMonsters,
    bool ShowTerrain,
    bool ShowPlayerBlip,
    // Monster HP-bar (nameplate) toggles by rarity.
    bool HpBarNormal,
    bool HpBarMagic,
    bool HpBarRare,
    bool HpBarUnique,
    // Smoothed guidance route per selected target, each carrying its selection-order color slot.
    IReadOnlyList<SelectedPath> SelectedPaths,
    // Legend rows (one per unified navigation target) for the HUD panel; never null. Each row already
    // carries its own IsSelected/ColorSlot (built at world rate), so the renderer never needs a predicate.
    IReadOnlyList<LegendEntry> Legend,
    // ── Collapsible "POE2Radar" navigation-menu widget (always drawn when Active+InGame). ──
    bool NavMenuExpanded,         // dropdown open?
    string NavMenuCorner,         // pinned corner: TopLeft/TopRight/BottomLeft/BottomRight
    // ── User-tweakable icon style table + HP-bar geometry (mirrored from RadarSettings). ──
    RadarStyles Styles,
    HpBarSettings HpBars,
    // Monster HP bars: style decided at world rate, position/HP refreshed live each render frame so bars
    // track moving mobs smoothly. Null/empty → none. Replaces the old per-frame resolve over all entities.
    IReadOnlyList<HpBarTarget>? HpBarTargets,
    // Walkable-terrain bitmap colors/transparency (mirrored from RadarSettings).
    TerrainSettings TerrainStyle,
    // Priced ground-item labels (unique drops) to draw over their in-world loot icons. Null/empty → none.
    IReadOnlyList<ItemLabel>? ItemLabels = null,
    // ── Unified display-rule engine (Phase 1). Resolves an entity to the first matching display rule
    // (or null → not drawn); the rule says hide or how to draw (shape/color/size/label). Replaces the
    // watched/mechanic/category dot decision in DrawMap. Null only if not wired (defensive). ──
    Func<Poe2Live.EntityDot, Web.DisplayRule?>? Resolve = null,
    // Tile-landmark styling resolver (Phase 2b): given a tile path, the matching "Tile"-category rule
    // (styling pass) or null. Lets a rule restyle/hide a surfaced landmark; null → default Landmark style.
    Func<string, Web.DisplayRule?>? ResolveTile = null,
    // ── Atlas overlay (takes precedence over the minimap/radar when the Atlas screen is open). ──
    bool AtlasOpen = false,                       // the Atlas screen is open → draw atlas highlights + route, suppress radar
    IReadOnlyList<AtlasMark>? AtlasNodes = null,   // tracked/arrowed nodes to highlight (canvas-space coords)
    // Atlas canvas→screen homography coefficients (h0..h7; h8=1). Shear/persp 0 ⇒ plain affine.
    float AtlasScale = 0.5f,   // h0
    float AtlasScaleY = 0.5f,  // h4
    float AtlasOffX = 0f,      // h2
    float AtlasOffY = 0f,      // h5
    float AtlasShearX = 0f,    // h1
    float AtlasShearY = 0f,    // h3
    float AtlasPersX = 0f,     // h6
    float AtlasPersY = 0f,     // h7
    // Atlas route (F10 workflow): START/END tiles in canvas-space (relPos), and the graph path between them.
    // Projected with the same atlas homography as the marks. Start/End draw as markers even before a path
    // exists; AtlasRoute (≥2 pts) is the graph polyline, else the renderer draws a straight START→END line.
    NumVec2? AtlasStart = null,
    NumVec2? AtlasEnd = null,
    IReadOnlyList<NumVec2>? AtlasRoute = null,
    // Auto-routing (improvement 1): the player's CURRENT atlas node (canvas-space relPos, drawn as a
    // "you are here" marker) and one route per tracked target from there/the accessible frontier. Both
    // projected with the atlas homography. Null/empty when auto-routing is off or no current node is known.
    NumVec2? AtlasCurrent = null,
    IReadOnlyList<AtlasRouteInfo>? AtlasAutoRoutes = null,
    // Draw a biome-coloured border around tracked atlas labels (improvement 2). Mirrored from settings.
    bool AtlasBiomeBorder = true,
    // Priced "Runeshape Combinations" reward labels (screen-space; drawn whenever the panel is open).
    IReadOnlyList<RuneLabel>? RuneLabels = null,
    // Priced ritual tribute-shop reward labels (screen-space; drawn whenever the shop is open).
    IReadOnlyList<RitualLabel>? RitualRewards = null,
    // Runeshape monoliths to mark on the map (value-coloured icon + N badge + value/reward label) and
    // list in the nearby-monolith panel. World-space (grid). Null/empty → none.
    IReadOnlyList<MonolithMarker>? Monoliths = null,
    bool ShowMonolithPanel = true,
    // Persisted click-to-collapse state for the nearby-monolith reward panel. Collapsed hides all reward
    // rows; only the title row (with caret) renders and the title-bar hit-rect toggles this flag via
    // OnOverlayClick. Mirrored from RadarSettings.MonolithPanelCollapsed at ctx assembly time.
    bool MonolithPanelCollapsed = false,
    // Persisted click-to-collapse state for the preload panel. Collapsed hides all hit rows; only the
    // title row (with caret) renders and the title-bar hit-rect toggles this flag via OnOverlayClick.
    // Mirrored from RadarSettings.PreloadPanelCollapsed at ctx assembly time. Default false because
    // the preload panel is a "look at this" surface — users should see it on first launch.
    bool PreloadPanelCollapsed = false,
    // Pre-sorted (desc BestEx), capped-to-6 slice of Monoliths for the panel rows — avoids per-frame
    // OrderByDescending(...).Take(6).ToList() in the renderer. Null/empty → none.
    IReadOnlyList<MonolithMarker>? MonolithsTop = null,
    // Display-rules generation counter — renderer clears its rule-color memo when this changes.
    int DisplayRulesGen = 0,
    // ── Session HUD (read-only pace/zone/death overlay). Both discrete fields, mirroring how
    // RenderContext carries Styles/HpBars/TerrainStyle/NavMenuCorner — there is no whole-RadarSettings
    // member. Session is null when the snapshot has not been published yet. ──
    POE2Radar.Core.Session.SessionStats?  Session            = null,
    Config.SessionHudSettings             SessionHudSettings = null!,
    // ── Patch-resilience health banner. Health picks the banner color; HealthMessage is the text
    // (null → no banner). Drawn whenever the overlay is Active and HealthMessage != null. ──
    POE2Radar.Core.Health.HealthState     Health             = POE2Radar.Core.Health.HealthState.Ok,
    string?                               HealthMessage      = null,
    // ── Campaign GPS: compact top-strip instruction line (null → no line drawn). ──
    string?                               CampaignGps        = null,
    // ── Zone summary panel: live counts for the current area (null → not yet ready / stale zone). ──
    ZoneSummary?                          ZoneSummary        = null,
    Config.ZoneSummarySettings?           ZoneSummaryHud     = null,
    // ── Affix nameplates: pre-read per-mob world positions + filtered affix lines, built at render rate
    // from the world tick's AffixNameplateSpec list (same HP-bar pattern: world reads live pos each frame).
    // Null/empty → none drawn. Settings mirrored so the renderer needs no settings reference. ──
    IReadOnlyList<AffixNameplateTarget>?  AffixTargets       = null,
    Config.AffixNameplateSettings?        AffixNameplates    = null,
    IReadOnlyList<BuffNameplateTarget>?   BuffTargets        = null,
    Config.BuffNameplateSettings?         BuffNameplates     = null,
    // Directional chevron spacing along atlas route lines (in chevron-heights). Default 8f matches upstream.
    float                                 AtlasRouteArrowSpacing = 8f,
    // #5 on-node content icons: draw content-type glyphs on FOGGED atlas nodes (the game hides these).
    // AtlasContentIcons mirrors AtlasShowContentIcons from settings; AtlasContentIconSize is the glyph size px.
    bool                                  AtlasContentIcons = true,
    float                                 AtlasContentIconSize = 26f,
    // Grid→canvas affine fit (world thread, from all on-screen nodes); null when it couldn't be fit.
    // The renderer uses it to place OFF-screen arrowed atlas nodes from their stable grid coordinate.
    POE2Radar.Core.AffineFit2D.Affine?    AtlasGridFit = null,
    // ── Preload Alert: zone-scoped hits (built once on zone entry, persisted until next zone change).
    // Gated on zone-load guard (PreloadEnabled && worldFresh && snap.AreaHash == _areaHash).
    // Null/empty → nothing drawn. Anchor/Offset mirror ZoneSummary/SessionHud layout conventions. ──
    IReadOnlyList<PreloadHit>?            PreloadHits = null,
    bool                                  PreloadEnabled = false,
    string                                PreloadAnchor = "top-right",
    int                                   PreloadOffsetX = 0,
    int                                   PreloadOffsetY = 0,
    // ── Off-screen entity arrows: per-frame list of world positions + colors + labels, built at render
    // rate from the world tick's EntityArrowSpecs list (same HP-bar pattern: world builds spec → snapshot
    // → render converts via _liveRender.TryLiveBarAt → RenderContext). Settings mirrored so the renderer
    // needs no settings reference. Null/empty → none drawn. ──
    IReadOnlyList<EntityArrowTarget>?     EntityArrows = null,
    bool                                  EntityArrowsEnabled = false,
    float                                 EntityArrowSize = 11f,
    bool                                  EntityArrowShowLabel = true,
    int                                   EntityArrowMax = 12,
    int                                   EntityArrowMinEdgePx = 24,
    // ── v0.29 Panels: session-transient overlay panels (boss cheat-sheet on zone entry, waystone risk on
    // Ctrl+Alt+W hotkey). Both close on user X-click OR next zone change. Mirrored from RadarApp fields
    // at snapshot-assembly time — never persisted. Null Entry/Result → panel not currently displayed;
    // Dismissed = user clicked [X]; Collapsed = user clicked caret (title bar stays, body hidden). ──
    POE2Radar.Core.Game.BossEncounterCatalog.EncounterEntry?  BossPanelEntry = null,
    bool                                                       BossPanelDismissed = false,
    bool                                                       BossPanelCollapsed = false,
    POE2Radar.Core.Game.WaystoneModRisk.WaystoneRiskResult?    WaystonePanelResult = null,
    bool                                                       WaystoneDismissed = false,
    bool                                                       WaystoneCollapsed = false,
    // v0.30 Instinct: prior-wipe count for the CURRENT character × current boss (from BossWipeLog).
    // 0 when unknown / no matching boss / char name unreadable. DrawBossPanel prepends "🪦 Nx before"
    // to the title bar when > 0.
    int                                                        BossPriorWipes = 0,
    // v0.30 Instinct: user's personal waystone red-flag list (from RadarSettings.WaystoneRedFlags),
    // mirrored so the render thread doesn't need a settings reference. Mods whose Name matches an
    // entry get a ★ prefix in DrawWaystonePanel regardless of the parser's tier verdict.
    IReadOnlyList<string>?                                     WaystoneRedFlags = null);
