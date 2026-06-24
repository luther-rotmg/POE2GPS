using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using System.Text.Json;

public class ObjectiveTierRankTests
{
    // ── Tier ordering ──────────────────────────────────────────────────────────
    [Fact]
    public void Rank_TierDescending_SeasonalEventBeforeSideBossBeforeExit()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("exit",    "Exit",    "MainProgression", 50, ObjectiveTier.Exit,          Enabled: true, Metadata: new(){"ExitToken"}),
            new CampaignObjective("boss",    "Boss",    "SideBoss",        50, ObjectiveTier.SideBoss,      Enabled: true, Metadata: new(){"BossToken"}),
            new CampaignObjective("league",  "League",  "League",          50, ObjectiveTier.SeasonalEvent, Enabled: true, Metadata: new(){"LeagueToken"}),
        });
        var entities = new List<Poe2Live.EntityDot>
        {
            MakeEntity(1, "ExitToken",   Poe2Live.EntityCategory.Other),
            MakeEntity(2, "BossToken",   Poe2Live.EntityCategory.Other),
            MakeEntity(3, "LeagueToken", Poe2Live.EntityCategory.Other),
        };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.Equal(3, result.Count);
        Assert.Equal(ObjectiveTier.SeasonalEvent, result[0].Tier);
        Assert.Equal(ObjectiveTier.SideBoss,      result[1].Tier);
        Assert.Equal(ObjectiveTier.Exit,           result[2].Tier);
    }

    [Fact]
    public void Rank_PriorityWithinSameTier_HigherFirst()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("b1", "Boss40", "SideBoss", 40, ObjectiveTier.SideBoss, Enabled: true, Metadata: new(){"B40"}),
            new CampaignObjective("b2", "Boss80", "SideBoss", 80, ObjectiveTier.SideBoss, Enabled: true, Metadata: new(){"B80"}),
        });
        var entities = new List<Poe2Live.EntityDot>
        {
            MakeEntity(1, "B40", Poe2Live.EntityCategory.Other),
            MakeEntity(2, "B80", Poe2Live.EntityCategory.Other),
        };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.Equal("Boss80", result[0].Label);   // B80 (priority 80) ranked first
        Assert.Equal(80, result[0].Priority);
    }

    [Fact]
    public void Rank_DistanceWithinSameTierAndPriority_NearerFirst()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("sz1", "SZ1", "SideZone", 60, ObjectiveTier.SideZone, Enabled: true, Metadata: new(){"SZ1Token"}),
            new CampaignObjective("sz2", "SZ2", "SideZone", 60, ObjectiveTier.SideZone, Enabled: true, Metadata: new(){"SZ2Token"}),
        });
        var entities = new List<Poe2Live.EntityDot>
        {
            MakeEntityAt(1, "SZ1Token", Poe2Live.EntityCategory.Other, new System.Numerics.Vector2(10, 0)),
            MakeEntityAt(2, "SZ2Token", Poe2Live.EntityCategory.Other, new System.Numerics.Vector2(5, 0)),
        };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.True(result[0].DistanceSq < result[1].DistanceSq);
    }

    [Fact]
    public void Rank_TierDominatesPriority_SideBossBeforeSideZone()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            new CampaignObjective("sz",   "SZ999", "SideZone", 999, ObjectiveTier.SideZone, Enabled: true, Metadata: new(){"SZToken"}),
            new CampaignObjective("boss", "Boss1", "SideBoss",   1, ObjectiveTier.SideBoss,  Enabled: true, Metadata: new(){"BossToken"}),
        });
        var entities = new List<Poe2Live.EntityDot>
        {
            MakeEntity(1, "SZToken",   Poe2Live.EntityCategory.Other),
            MakeEntity(2, "BossToken", Poe2Live.EntityCategory.Other),
        };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.Equal(ObjectiveTier.SideBoss, result[0].Tier);
    }

    // ── DefaultTierForCategory ─────────────────────────────────────────────────
    [Fact] public void DefaultTier_League_IsSeasonalEvent()      => Assert.Equal(ObjectiveTier.SeasonalEvent, CampaignObjective.DefaultTierForCategory("League"));
    [Fact] public void DefaultTier_SideBoss_IsSideBoss()         => Assert.Equal(ObjectiveTier.SideBoss,      CampaignObjective.DefaultTierForCategory("SideBoss"));
    [Fact] public void DefaultTier_SideZone_IsSideZone()         => Assert.Equal(ObjectiveTier.SideZone,      CampaignObjective.DefaultTierForCategory("SideZone"));
    [Fact] public void DefaultTier_MainProgression_IsExit()       => Assert.Equal(ObjectiveTier.Exit,          CampaignObjective.DefaultTierForCategory("MainProgression"));
    [Fact] public void DefaultTier_Unknown_IsExit()               => Assert.Equal(ObjectiveTier.Exit,          CampaignObjective.DefaultTierForCategory("UnknownCategory"));
    [Fact] public void DefaultTier_Null_IsExit()                  => Assert.Equal(ObjectiveTier.Exit,          CampaignObjective.DefaultTierForCategory(null));

    // ── Null Tier resolved in Consider() ──────────────────────────────────────
    [Fact]
    public void NullTier_ResolvesFromCategory_InConsider()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            // Tier = null — should resolve to SeasonalEvent via Category "League"
            new CampaignObjective("league", "League Mechanic", "League", 100, null, Enabled: true, Metadata: new(){"LeagueToken"}),
        });
        var entities = new List<Poe2Live.EntityDot> { MakeEntity(1, "LeagueToken", Poe2Live.EntityCategory.Other) };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.Single(result);
        Assert.Equal(ObjectiveTier.SeasonalEvent, result[0].Tier);
    }

    // ── Explicit Tier overrides Category default ───────────────────────────────
    [Fact]
    public void ExplicitTier_OverridesCategoryDefault()
    {
        var cat = new ObjectiveCatalog(new[]
        {
            // Category "League" would default to SeasonalEvent but Tier=Bonus is explicit
            new CampaignObjective("x", "X", "League", 100, ObjectiveTier.Bonus, Enabled: true, Metadata: new(){"XToken"}),
        });
        var entities = new List<Poe2Live.EntityDot> { MakeEntity(1, "XToken", Poe2Live.EntityCategory.Other) };
        var result = cat.Rank(entities, Array.Empty<Poe2Live.Landmark>(), default);
        Assert.Single(result);
        Assert.Equal(ObjectiveTier.Bonus, result[0].Tier);
    }

    // ── Backward-compatible deserialization ───────────────────────────────────
    [Fact]
    public void Deserialize_NoTierKey_TierIsNull()
    {
        const string json = "[{\"id\":\"x\",\"label\":\"X\",\"category\":\"SideBoss\",\"priority\":80}]";
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var result = JsonSerializer.Deserialize<List<CampaignObjective>>(json, opts)!;
        Assert.Single(result);
        Assert.Null(result[0].Tier);
    }

    // ── Tier round-trips as string ─────────────────────────────────────────────
    [Fact]
    public void Serialize_Tier_AsString()
    {
        var obj = new CampaignObjective("id", "Label", "SideBoss", 80, ObjectiveTier.SideBoss);
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(obj, opts);
        Assert.Contains("\"SideBoss\"", json);
        Assert.DoesNotContain("\"3\"", json);
        Assert.DoesNotContain(":3", json);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private static Poe2Live.EntityDot MakeEntity(uint id, string meta, Poe2Live.EntityCategory cat) =>
        MakeEntityAt(id, meta, cat, default);

    private static Poe2Live.EntityDot MakeEntityAt(uint id, string meta, Poe2Live.EntityCategory cat, System.Numerics.Vector2 grid) =>
        new(id, 0, grid, default, cat, meta, 0, 0, false, 0, Poe2Live.Rarity.Normal, false);
}
