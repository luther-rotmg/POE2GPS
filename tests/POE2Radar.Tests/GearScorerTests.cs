using POE2Radar.Core.Gear;

public class GearScorerTests
{
    private static StatWeights Weights(double target = 100, double threshold = 90, params (string id, double w)[] ws)
        => new(ws.ToDictionary(x => x.id, x => x.w), target, threshold);

    private static Affix A(string line, double value, params string[] ids) => new(line, ids, value);

    [Fact]
    public void WeightedSum_ScaledToTarget()
    {
        var w = Weights(100, 90, ("life", 1.0));
        var s = GearScorer.Score(new[] { A("+50 Life", 50, "life") }, w);
        Assert.Equal(50, s.Score, 3);   // 50 * 1.0 / 100 * 100
        Assert.False(s.IsGodRoll);
    }

    [Fact]
    public void ClampsAt100()
    {
        var w = Weights(100, 90, ("life", 1.0));
        var s = GearScorer.Score(new[] { A("+250 Life", 250, "life") }, w);
        Assert.Equal(100, s.Score, 3);
        Assert.True(s.IsGodRoll);
    }

    [Fact]
    public void UnweightedAffix_ContributesZero()
    {
        var w = Weights(100, 90, ("life", 1.0));
        var s = GearScorer.Score(new[] { A("+30% Cold Resist", 30, "coldres") }, w);
        Assert.Equal(0, s.Score, 3);
        Assert.Single(s.Affixes);
        Assert.Equal(0, s.Affixes[0].Points, 3);
    }

    [Fact]
    public void MultiStatAffix_TakesMaxWeight()
    {
        var w = Weights(100, 90, ("a", 0.5), ("b", 1.0));
        var s = GearScorer.Score(new[] { A("hybrid", 40, "a", "b") }, w);
        Assert.Equal(40, s.Score, 3);   // 40 * max(0.5,1.0)=1.0 / 100 * 100
        Assert.Equal(1.0, s.Affixes[0].Weight, 3);
    }

    [Fact]
    public void EmptyItem_ScoresZero_NotGodRoll()
    {
        var s = GearScorer.Score(System.Array.Empty<Affix>(), Weights(100, 90, ("life", 1.0)));
        Assert.Equal(0, s.Score, 3);
        Assert.False(s.IsGodRoll);
        Assert.Empty(s.Affixes);
    }

    [Fact]
    public void ThresholdBoundary_IsInclusive()
    {
        var w = Weights(100, 90, ("life", 1.0));
        var s = GearScorer.Score(new[] { A("+90 Life", 90, "life") }, w);
        Assert.Equal(90, s.Score, 3);
        Assert.True(s.IsGodRoll);   // >= threshold
    }

    [Fact]
    public void Contributions_SumToRaw()
    {
        var w = Weights(100, 90, ("life", 1.0), ("es", 0.5));
        var s = GearScorer.Score(new[] { A("+40 Life", 40, "life"), A("+60 ES", 60, "es") }, w);
        // 40*1.0 + 60*0.5 = 70 -> score 70
        Assert.Equal(70, s.Score, 3);
        Assert.Equal(70, s.Affixes.Sum(c => c.Points), 3);
    }

    [Fact]
    public void DegenerateTarget_DoesNotThrow()
    {
        var s = GearScorer.Score(new[] { A("+50 Life", 50, "life") }, Weights(0, 90, ("life", 1.0)));
        Assert.Equal(100, s.Score, 3); // target<=0 falls back to 1 → clamps to 100
    }
}
