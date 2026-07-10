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

    // Reach — Long #38 (v0.26): localized map names via MapMeta.LocalizedName + MapMeta.Translates.

    [Fact] public void Translates_loaded_for_known_map()
    {
        // MapAlpineRidge is a shipped archetype with a translates block covering 10 languages.
        var meta = AtlasMapData.Shared.Get("MapAlpineRidge");
        Assert.NotNull(meta);
        Assert.Equal("Alpine Ridge", meta!.Value.Name);
        Assert.NotNull(meta.Value.Translates);
        Assert.True(meta.Value.Translates!.ContainsKey("french"),
            $"expected 'french' key in translates dict, got: {string.Join(',', meta.Value.Translates.Keys)}");
    }

    [Fact] public void LocalizedName_returns_translation_for_known_language()
    {
        var meta = AtlasMapData.Shared.Get("MapAlpineRidge");
        Assert.NotNull(meta);
        var frName = meta!.Value.LocalizedName("french");
        // Whatever the shipped french string is, it should NOT equal the English name (Alpine Ridge)
        // and should be non-empty.
        Assert.False(string.IsNullOrEmpty(frName));
        Assert.NotEqual("Alpine Ridge", frName);
    }

    [Fact] public void LocalizedName_falls_back_to_english_when_lang_missing_or_null()
    {
        var meta = AtlasMapData.Shared.Get("MapAlpineRidge");
        Assert.NotNull(meta);
        Assert.Equal("Alpine Ridge", meta!.Value.LocalizedName(null));
        Assert.Equal("Alpine Ridge", meta.Value.LocalizedName(""));
        Assert.Equal("Alpine Ridge", meta.Value.LocalizedName("klingon"));  // unknown lang key → English fallback
    }

    [Fact] public void LocalizedName_falls_back_when_no_translates_block()
    {
        // A map with no translates block still returns the top-level name from LocalizedName.
        // Construct a synthetic MapMeta with Translates=null and confirm.
        var meta = new AtlasMapData.MapMeta("Test Zone", "normal", "map",
            System.Array.Empty<string>(), Translates: null);
        Assert.Equal("Test Zone", meta.LocalizedName("french"));
        Assert.Equal("Test Zone", meta.LocalizedName("english"));
    }
}
