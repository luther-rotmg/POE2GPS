using POE2Radar.Core.Campaign;

public class ObjectiveClassifierTests
{
    [Fact]
    public void Classify_BreachMetadata_ReturnsBonusLeagueHigh()
    {
        // Rule 1 fires before rule 9 (league name in metadata takes priority over BossArena+Unique)
        var r = ObjectiveClassifier.Classify("Metadata/Monsters/Breach/BreachOpener", "Monster", false, "Unique");
        Assert.NotNull(r);
        Assert.Equal(ObjectiveTier.Bonus,          r!.Value.Tier);
        Assert.Equal("League",                     r.Value.SuggestedCategory);
        Assert.Equal(ClassifyConfidence.High,      r.Value.Confidence);
    }

    [Fact]
    public void Classify_BossMonsterUnique_ReturnsSideBossHigh()
    {
        // No league name; rule 9 fires: BossArena + Monster + Unique
        var r = ObjectiveClassifier.Classify("Metadata/Monsters/BossArena/ActBoss", "Monster", false, "Unique");
        Assert.NotNull(r);
        Assert.Equal(ObjectiveTier.SideBoss,       r!.Value.Tier);
        Assert.Equal(ClassifyConfidence.High,      r.Value.Confidence);
    }

    [Fact]
    public void Classify_TransitionCategory_ReturnsExitHigh()
    {
        // Rule 11 fires: metadata contains "Transition" + category Transition
        var r = ObjectiveClassifier.Classify("Metadata/Terrain/Transition/AreaTransition", "Transition", false, "Normal");
        Assert.NotNull(r);
        Assert.Equal(ObjectiveTier.Exit,           r!.Value.Tier);
        Assert.Equal("Transition",                 r.Value.SuggestedCategory);
        Assert.Equal(ClassifyConfidence.High,      r.Value.Confidence);
    }

    [Fact]
    public void Classify_UnknownObject_ReturnsNull()
    {
        // No rule matches: category Object with unrecognized metadata, poi=false, Normal
        var r = ObjectiveClassifier.Classify("Metadata/Terrain/Rock", "Object", false, "Normal");
        Assert.Null(r);
    }

    [Fact]
    public void Classify_NullMetadata_DoesNotThrow()
    {
        // null metadata must not throw; no rule can match a null path
        var r = ObjectiveClassifier.Classify(null, "Other", false, "Normal");
        Assert.Null(r);
    }

    [Fact]
    public void Classify_UniquePoiMonster_FallbackMedium()
    {
        // Rule 10 fires: metadata contains "Boss" + category Monster + poi=true
        var r = ObjectiveClassifier.Classify("Metadata/Monsters/SomeRare/SomeBoss", "Monster", true, "Normal");
        Assert.NotNull(r);
        Assert.Equal(ObjectiveTier.SideBoss,       r!.Value.Tier);
        Assert.Equal(ClassifyConfidence.Medium,    r.Value.Confidence);
    }

    [Fact]
    public void Classify_AllTransitionCategoryNoMetadataMatch_ExitMedium()
    {
        // Rules 11-13 do not match (no Transition/Exit/WorldArea/AreaTransition in metadata).
        // Rule 14: catch-all Transition category -> Exit Medium
        var r = ObjectiveClassifier.Classify("Metadata/Terrain/Waypoint", "Transition", false, "Normal");
        Assert.NotNull(r);
        Assert.Equal(ObjectiveTier.Exit,           r!.Value.Tier);
        Assert.Equal(ClassifyConfidence.Medium,    r.Value.Confidence);
    }
}
