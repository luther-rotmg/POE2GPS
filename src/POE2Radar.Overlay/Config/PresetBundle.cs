using System.Collections.Generic;
using POE2Radar.Overlay.Web;     // DisplayRule

namespace POE2Radar.Overlay.Config;

/// <summary>A shareable "radar look": visual config only. Operational/identity/calibration settings
/// and one-time migration guards are deliberately ABSENT (importing them would corrupt the user's
/// state). Metadata fields are never applied to settings.</summary>
public sealed class PresetBundle
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";

    public RadarStyles? Styles { get; set; }
    public HpBarSettings? HpBars { get; set; }
    public TerrainSettings? Terrain { get; set; }
    public GroundItemSettings? GroundItems { get; set; }
    public bool? ShowMonsters { get; set; }
    public bool? ShowTerrain { get; set; }
    public bool? ShowPlayerBlip { get; set; }
    public bool? HpBarNormal { get; set; }
    public bool? HpBarMagic { get; set; }
    public bool? HpBarRare { get; set; }
    public bool? HpBarUnique { get; set; }
    public List<DisplayRule>? DisplayRules { get; set; }
}
