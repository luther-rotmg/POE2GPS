using POE2Radar.Core.Campaign;

public class ObjectiveCatalogCoversTests
{
    private static SeenPoi EntitySeen(string metadata, string category = "Object", bool poi = false, string rarity = "Normal")
        => new("e:" + metadata, metadata, null, category, poi, rarity, "name", "zone", 1, System.DateTime.UnixEpoch);
    private static SeenPoi TileSeen(string path)
        => new("t:" + path, null, path, "Tile", false, "", "name", "zone", 1, System.DateTime.UnixEpoch);

    private static readonly ObjectiveCatalog Cat = new(new[]
    {
        new CampaignObjective("event", "Event", "League", 100, true, Metadata: new() { "RunesOfAldur" }),
        new CampaignObjective("exit", "Exit", "MainProgression", 10, true, Categories: new() { "Transition" }),
        new CampaignObjective("arena", "Arena", "Bosses", 70, true, LandmarkPath: new() { "*Arena*" }),
        new CampaignObjective("off", "Off", "X", 50, false, Metadata: new() { "ShouldNotMatch" }),
    });

    [Fact] public void Covers_MatchingEntityMetadata()
        => Assert.True(Cat.Covers(EntitySeen("Metadata/Events/RunesOfAldur")));

    [Fact] public void Covers_MatchingCategory()
        => Assert.True(Cat.Covers(EntitySeen("Metadata/Transition/Door", category: "Transition")));

    [Fact] public void Covers_MatchingLandmarkPath()
        => Assert.True(Cat.Covers(TileSeen("Maps/Arena/Boss.tdt")));

    [Fact] public void DoesNotCover_Unmatched()
        => Assert.False(Cat.Covers(EntitySeen("Metadata/Misc/Rock")));

    [Fact] public void DoesNotCover_DisabledObjective()
        => Assert.False(Cat.Covers(EntitySeen("Metadata/ShouldNotMatch")));

    [Fact] public void EntityObjective_DoesNotCoverLandmark()
        => Assert.False(Cat.Covers(TileSeen("Metadata/Events/RunesOfAldur"))); // event is entity-only
}
