using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class PoiCandidateTests
{
    private static Poe2Live.EntityDot E(Poe2Live.EntityCategory cat, string meta = "m",
        bool poi = false, Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal)
        => new(1, 0, default, default, cat, meta, 1, 1, poi, 0, rarity, false);

    [Fact] public void Keeps_PoiFlaggedEntity()
        => Assert.True(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster, poi: true)));

    [Fact] public void Keeps_UniqueMonster()
        => Assert.True(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster, rarity: Poe2Live.Rarity.Unique)));

    [Theory]
    [InlineData(Poe2Live.EntityCategory.Npc)]
    [InlineData(Poe2Live.EntityCategory.Chest)]
    [InlineData(Poe2Live.EntityCategory.Transition)]
    [InlineData(Poe2Live.EntityCategory.Object)]
    public void Keeps_NotableCategories(Poe2Live.EntityCategory cat)
        => Assert.True(PoiCandidate.IsCandidate(E(cat)));

    [Fact] public void Skips_OrdinaryMonster()
        => Assert.False(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Monster)));

    [Fact] public void Skips_PlainOther()
        => Assert.False(PoiCandidate.IsCandidate(E(Poe2Live.EntityCategory.Other)));

    [Fact] public void EntitySignature_KeysByMetadata()
    {
        Assert.Equal(PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")),
                     PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")));
        Assert.NotEqual(PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/X")),
                        PoiCandidate.EntitySignature(E(Poe2Live.EntityCategory.Object, "Metadata/Y")));
    }

    [Fact] public void LandmarkSignature_KeysByPath()
        => Assert.Equal("t:Maps/A.tdt", PoiCandidate.LandmarkSignature(new Poe2Live.Landmark("n", "Maps/A.tdt", default, 1)));
}
