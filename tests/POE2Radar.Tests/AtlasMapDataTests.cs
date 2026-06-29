using POE2Radar.Core.Game;

public class AtlasMapDataTests
{
    [Fact] public void Embedded_tables_load_nonempty()
    {
        Assert.True(AtlasMapData.Shared.MapCount > 0, "atlas_maps.json should load many archetypes");
        Assert.True(AtlasMapData.Shared.ContentCount > 0, "atlas_content.json should load content");
    }

    [Fact] public void Unknown_mapid_degrades_gracefully()
    {
        Assert.False(AtlasMapData.Shared.TryGet("DefinitelyNotAMapId", out _));
        Assert.Null(AtlasMapData.Shared.Get("DefinitelyNotAMapId"));
        Assert.Null(AtlasMapData.Shared.ContentDesc("NotAContent"));
    }

    [Fact] public void Special_badge_seeded()
        => Assert.Equal("AtlasIconContentGigaMirror", AtlasMapData.Shared.ContentIcon("Grand Mirror"));
}
