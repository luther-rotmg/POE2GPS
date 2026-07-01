using POE2Radar.Core.Game;

public class PreloadCatalogTests
{
    [Fact] public void Loads_rules() => Assert.True(PreloadCatalog.Shared.RuleCount > 20);

    [Fact] public void Matches_known_boss()
    {
        // match rule: "metadata/monsters/breach/breachoverseerboss/" (tier: pinnacle)
        var hit = PreloadCatalog.Shared.Match("metadata/monsters/breach/breachoverseerboss/itthatreturned");
        Assert.NotNull(hit);
        Assert.Equal("pinnacle", hit!.Tier);
    }

    [Fact] public void Noise_path_is_gated_out()
        => Assert.Null(PreloadCatalog.Shared.Match("art/models/monsters/honeyant/rig.amd")); // art/ not a gateRoot

    [Fact] public void Unknown_content_path_returns_null()
        => Assert.Null(PreloadCatalog.Shared.Match("metadata/monsters/nothingspecial/whatever"));
}
