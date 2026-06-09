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

/// <summary>One atlas node to highlight. <see cref="X"/>/<see cref="Y"/> are the node's canvas-space
/// RelativePos; the renderer projects them to screen via the atlas transform (scale + offset).</summary>
public readonly record struct AtlasMark(float X, float Y, bool Selected, bool HasContent, bool Visited, bool Unlocked, int Biome, int IconType, string? Label = null, string? Color = null, bool Arrow = false);

/// <summary>What the PoE2 renderer needs each frame. Built fresh by <see cref="RadarApp"/>.</summary>
public sealed record RenderContext(
    bool InGame,
    bool Active,            // PoE2 is the foreground window — draw nothing when false
    int WindowWidth,
    int WindowHeight,
    NumVec2 PlayerGrid,
    Poe2Live.MapUi Map,
    IReadOnlyList<Poe2Live.EntityDot> Entities,
    IReadOnlyList<Poe2Live.Landmark> Landmarks,
    uint AreaHash,
    Poe2Live.TerrainData? Terrain,
    // Live projection calibration (adjustable at runtime).
    float ScaleMul,
    float OffsetX,
    float OffsetY,
    // Auto-flask status.
    float HpPct,
    float ManaPct,
    float EsPct,
    string FlaskNote,
    // Area / character HUD.
    string AreaCode,
    int CharLevel,
    // WorldToScreen matrix (16 floats, row-major) for world-space nameplates; null if unavailable.
    float[]? CameraMatrix,
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
    // Predicate: is this navigation-target id currently selected? (drives the legend swatch/highlight).
    Func<string, bool> IsSelected,
    // Legend rows (one per unified navigation target) for the HUD panel; never null.
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
    IReadOnlyList<NumVec2>? AtlasRoute = null);
