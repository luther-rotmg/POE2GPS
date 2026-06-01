using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// User-tweakable overlay settings, persisted as JSON next to the executable
/// (<c>config/radar_settings.json</c>). Defaults reproduce the original hardcoded behavior exactly,
/// so a missing/partial file changes nothing. Calibration is saved live as hotkeys adjust it.
/// </summary>
public sealed class RadarSettings
{
    // ── Feature flags (reserved for later phases; no behavior wired yet). ──
    public bool HideJunk { get; set; } = false;
    public bool ShowPath { get; set; } = false;
    public bool UseCuratedLandmarks { get; set; } = true;
    public bool DrawAllLandmarkPaths { get; set; } = false;

    // ── Radar display toggles. ──
    public bool ShowMonsters { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;

    // ── Navigation-menu widget: which screen corner it is pinned to.
    //    One of "TopLeft", "TopRight", "BottomLeft", "BottomRight". ──
    public string NavMenuCorner { get; set; } = "TopLeft";

    // ── Persistent auto-nav: substrings matched (case-insensitive Contains) against a navigation
    //    target's MatchKey (tile path / entity metadata). On every zone change, every target whose
    //    MatchKey matches ANY pattern is auto-selected (up to the 8-color cap), so entering a new
    //    zone auto-draws a path to e.g. the expedition encounter. Seeded with one example so the
    //    feature is visible out of the box; clear the list to disable. ──
    public List<string> AutoNavPatterns { get; set; } = new() { "ExpeditionEncounter" };

    // ── Monster HP bars (world-space nameplates) by rarity.
    //    Defaults preserve prior behavior: Magic/Rare/Unique shown, Normal hidden. ──
    public bool HpBarNormal { get; set; } = false;
    public bool HpBarMagic { get; set; } = true;
    public bool HpBarRare { get; set; } = true;
    public bool HpBarUnique { get; set; } = true;

    // ── Projection calibration (PageUp/Down = scale, arrows = offset, Home = reset). ──
    public float ScaleMul { get; set; } = 1.0f;
    public float OffX { get; set; } = 0f;
    public float OffY { get; set; } = 0f;

    // ── Auto-flask thresholds + per-flask cooldowns (milliseconds). ──
    public float LifeThresholdPct { get; set; } = 65f;
    public float ManaThresholdPct { get; set; } = 30f;
    public int LifeCooldownMs { get; set; } = 2500;
    public int ManaCooldownMs { get; set; } = 2000;

    // ── Flask key codes (Win32 virtual-key). Defaults: '1' = life, '2' = mana. ──
    public int LifeKey { get; set; } = 0x31;
    public int ManaKey { get; set; } = 0x32;

    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    // ── Per-item icon styling (shape / color / opacity / size) + metadata-matched "mechanic"
    //    overrides. Defaults reproduce the original hardcoded look exactly. ──
    public RadarStyles Styles { get; set; } = new();

    // ── Monster HP-bar geometry (the per-rarity ENABLE flags above stay the source of truth;
    //    this only adds sizing/offset). Rarity is signaled by a scaling border weight. ──
    public HpBarSettings HpBars { get; set; } = new();

    // ── Walkable-terrain bitmap colors/transparency. Defaults reproduce the old hardcoded wash. ──
    public TerrainSettings Terrain { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Config file path: a "config" directory next to the executable.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "radar_settings.json");

    /// <summary>
    /// Load settings from disk. Returns defaults if the file is missing (and writes a default file),
    /// and is tolerant of partial/missing keys. Never throws on IO/parse errors — logs and falls back.
    /// </summary>
    public static RadarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new RadarSettings();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<RadarSettings>(json, Json);
            return loaded ?? new RadarSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings load failed ({ex.Message}); using defaults.");
            return new RadarSettings();
        }
    }

    /// <summary>Persist current settings to disk. Never throws on IO error — logs and continues.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// A single drawable radar icon: shape, RGB color, opacity, pixel size, and an enable toggle.
/// <see cref="Shape"/> is one of Circle/Triangle/Star/Diamond/Plus/Square (anything else falls back
/// to Circle when rendered); <see cref="Color"/> is <c>#RRGGBB</c>; <see cref="Opacity"/> is 0..1.
/// </summary>
public sealed class IconStyle
{
    public bool Enabled { get; set; } = true;
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 3.0f;

    public IconStyle() { }
    public IconStyle(string shape, string color, float opacity, float size)
    {
        Shape = shape; Color = color; Opacity = opacity; Size = size;
    }
}

/// <summary>
/// A user-defined "mechanic" highlight: when an entity's metadata contains ANY of <see cref="Match"/>
/// (case-insensitive), it draws this icon instead of its generic category dot — so e.g. an Expedition
/// marker or a Strongbox stands out. The first enabled matching rule wins.
/// </summary>
public sealed class MechanicStyle
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = new();
    public string Shape { get; set; } = "Star";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 6.0f;
}

/// <summary>
/// Monster HP-bar geometry. Width is per-rarity; height + X/Y offset are shared. The per-rarity
/// enable flags live on <see cref="RadarSettings"/> (HpBarNormal/Magic/Rare/Unique); the bar/border
/// color is taken from the matching monster icon color so "rare = gold" stays one setting.
/// </summary>
public sealed class HpBarSettings
{
    public float Height { get; set; } = 5f;
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = -30f; // px relative to the mob's screen position (neg = up)
    public float WidthNormal { get; set; } = 30f;
    public float WidthMagic { get; set; } = 38f;
    public float WidthRare { get; set; } = 50f;
    public float WidthUnique { get; set; } = 64f;
}

/// <summary>
/// Walkable-terrain bitmap styling: the interior "wash" over walkable cells and the brighter
/// outline drawn on walkable cells bordering a wall/edge. Color is <c>#RRGGBB</c>; opacity is 0..1
/// (baked into the per-pixel alpha). Defaults reproduce the formerly hardcoded look exactly:
/// interior <c>#506482</c> @ ~30/255, edge <c>#3CDCFF</c> @ ~180/255. The per-area terrain bitmap
/// is rebuilt when any of these change.
/// </summary>
public sealed class TerrainSettings
{
    public string InteriorColor { get; set; } = "#506482";
    public float InteriorOpacity { get; set; } = 0.118f; // → 30/255
    public string EdgeColor { get; set; } = "#3CDCFF";
    public float EdgeOpacity { get; set; } = 0.706f;      // → 180/255
}

/// <summary>
/// The full radar icon style table. Every default mirrors the formerly hardcoded values in
/// <c>OverlayRenderer</c>, so a missing/partial config renders identically to before.
/// </summary>
public sealed class RadarStyles
{
    // Monster dots by rarity.
    public IconStyle MonsterNormal { get; set; } = new("Circle",   "#FF3333", 0.95f, 2.6f);
    public IconStyle MonsterMagic  { get; set; } = new("Diamond",  "#73A6FF", 0.97f, 3.4f);
    public IconStyle MonsterRare   { get; set; } = new("Triangle", "#FFD926", 1.00f, 5.5f);
    public IconStyle MonsterUnique { get; set; } = new("Star",     "#FF7300", 1.00f, 6.5f);

    // Other entity categories.
    public IconStyle Player        { get; set; } = new("Circle",  "#4DF2FF", 1.00f, 3.0f);
    public IconStyle Npc           { get; set; } = new("Plus",    "#FFD933", 0.95f, 4.0f);
    public IconStyle ChestRare     { get; set; } = new("Square",  "#FFD926", 0.95f, 5.0f);
    public IconStyle ChestUnique   { get; set; } = new("Square",  "#FF7300", 0.95f, 5.0f);
    public IconStyle Transition    { get; set; } = new("Diamond", "#66FF99", 0.95f, 4.5f);
    public IconStyle Poi           { get; set; } = new("Circle",  "#8CBFFF", 0.70f, 3.0f);

    // Tile landmarks (shape marker + text label at the group centroid).
    public IconStyle Landmark      { get; set; } = new("Diamond", "#F259F2", 1.00f, 5.0f);

    // Metadata-matched overrides (first enabled match wins). Seeded with common PoE2 mechanics.
    public List<MechanicStyle> Mechanics { get; set; } = new()
    {
        new() { Name = "Expedition", Match = new() { "ExpeditionEncounter", "Expedition" }, Shape = "Plus",     Color = "#26E6D9", Opacity = 1f, Size = 7f },
        new() { Name = "Ritual",     Match = new() { "Ritual" },                            Shape = "Star",     Color = "#FF3355", Opacity = 1f, Size = 7f },
        new() { Name = "Breach",     Match = new() { "Breach" },                            Shape = "Diamond",  Color = "#A64DFF", Opacity = 1f, Size = 7f },
        new() { Name = "Strongbox",  Match = new() { "Strongbox", "StrongBoxes" },          Shape = "Square",   Color = "#FFB300", Opacity = 1f, Size = 6f },
        new() { Name = "Essence",    Match = new() { "Essence" },                           Shape = "Triangle", Color = "#33E0FF", Opacity = 1f, Size = 7f },
        new() { Name = "Shrine",     Match = new() { "Shrine" },                            Shape = "Star",     Color = "#7DFF7D", Opacity = 1f, Size = 6f },
    };
}
