using Vector2 = System.Numerics.Vector2;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

public class ObjectiveCatalogTests
{
    private static Poe2Live.EntityDot Ent(uint id, string metadata, Vector2 grid,
        Poe2Live.EntityCategory cat = Poe2Live.EntityCategory.Monster, bool poi = false,
        Poe2Live.Rarity rarity = Poe2Live.Rarity.Normal)
        => new(id, 0, grid, default, cat, metadata, 1, 1, poi, 0, rarity, false);

    private static Poe2Live.Landmark Lm(string path, Vector2 center)
        => new("name", path, center, 1);

    private static readonly CampaignObjective[] Seed =
    {
        new("event", "Event", "League", 100, Enabled: true, Metadata: new() { "RunesOfAldur" }),
        new("boss", "Boss", "SideBoss", 80, Enabled: true, Metadata: new() { "*Ascendancy*" }),
        new("exit", "Continue", "MainProgression", 10, Enabled: true,
            Categories: new() { "Transition" }),
    };

    [Fact]
    public void Rank_OrdersByPriorityThenDistance()
    {
        var cat = new ObjectiveCatalog(Seed);
        var entities = new[]
        {
            Ent(1, "Metadata/Transition/Door", new Vector2(1, 0), Poe2Live.EntityCategory.Transition), // exit, prio 10, near
            Ent(2, "Metadata/Events/RunesOfAldur", new Vector2(50, 0)),                                 // event, prio 100, far
            Ent(3, "Metadata/Bosses/AscendancyTrial", new Vector2(5, 0)),                               // boss, prio 80
        };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Equal(new[] { "e:2", "e:3", "e:1" }, ranked.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Rank_NearestWinsWithinTier()
    {
        var cat = new ObjectiveCatalog(Seed);
        var entities = new[]
        {
            Ent(10, "Metadata/Transition/A", new Vector2(20, 0), Poe2Live.EntityCategory.Transition),
            Ent(11, "Metadata/Transition/B", new Vector2(3, 0), Poe2Live.EntityCategory.Transition),
        };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Equal("e:11", ranked[0].Id); // nearer transition wins the MainProgression tier
    }

    [Fact]
    public void Rank_SkipsDisabledAndUnmatched()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("event", "Event", "League", 100, Enabled: false,
                Metadata: new() { "RunesOfAldur" }),
        });
        var entities = new[] { Ent(1, "Metadata/Events/RunesOfAldur", Vector2.Zero) };
        Assert.Empty(cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero));
    }

    [Fact]
    public void Rank_MatchesLandmarkByPath()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("arena", "Arena", "Bosses", 70, Enabled: true,
                LandmarkPath: new() { "*Arena*" }),
        });
        var landmarks = new[] { Lm("Maps/Arena/Boss.tdt", new Vector2(4, 0)) };
        var ranked = cat.Rank(Array.Empty<Poe2Live.EntityDot>(), landmarks, Vector2.Zero);
        Assert.Single(ranked);
        Assert.StartsWith("t:Maps/Arena/Boss.tdt@", ranked[0].Id);
    }

    [Fact]
    public void Rank_KeepsHighestPriorityWhenEntityMatchesMultiple()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("a", "A", "X", 10, Enabled: true, Metadata: new() { "Boss" }),
            new CampaignObjective("b", "B", "Y", 90, Enabled: true, Metadata: new() { "Boss" }),
        });
        var entities = new[] { Ent(1, "Metadata/Boss", Vector2.Zero) };
        var ranked = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), Vector2.Zero);
        Assert.Single(ranked);
        Assert.Equal(90, ranked[0].Priority);
    }
}
