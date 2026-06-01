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
public readonly record struct NavTarget(string Id, string Name, NumVec2 Grid, string MatchKey, bool IsEntity);

/// <summary>One legend row: a navigation target, the selection-order color slot it draws in (0..7, or
/// -1 when unselected), and whether it is currently selected (its own A* route is drawn).</summary>
public readonly record struct LegendEntry(NavTarget Target, int ColorSlot, bool IsSelected);

/// <summary>One selected target's smoothed A* route: the selection-order color slot (0..7) used to pick
/// its draw/legend color and the smoothed grid-cell waypoints. Empty <see cref="Points"/> = no path.</summary>
public readonly record struct SelectedPath(int ColorSlot, IReadOnlyList<(int x, int y)> Points);

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
    // Walkable-terrain bitmap colors/transparency (mirrored from RadarSettings).
    TerrainSettings TerrainStyle);
