// tests/POE2Radar.Tests/GearScorerNormTests.cs
using POE2Radar.Core.Gear;
using Xunit;

namespace POE2Radar.Tests;

public class GearScorerNormTests
{
    private static Affix A(string id, double v) => new("line", new[] { id }, v);

    [Fact]
    public void norm_present_divides_value_before_weighting()
    {
        // life rolled 100, norm (median) 115, weight (pct) 20 → contribution = 100/115*20 ≈ 17.39 → score ≈ 17.39
        var w = new StatWeights(new Dictionary<string, double> { ["base_maximum_life"] = 20 }, 100, 85,
            new Dictionary<string, double> { ["base_maximum_life"] = 115 });
        var s = GearScorer.Score(new[] { A("base_maximum_life", 100) }, w);
        Assert.InRange(s.Score, 17.0, 17.8);
        Assert.Equal("base_maximum_life", Assert.Single(s.Affixes[0].StatIds));
    }

    [Fact]
    public void norm_absent_falls_back_to_times_one()
    {
        // no NormById → norm=1 → contribution = value*weight = 2*5 = 10
        var w = new StatWeights(new Dictionary<string, double> { ["x"] = 5 }, 100, 85);
        var s = GearScorer.Score(new[] { A("x", 2) }, w);
        Assert.InRange(s.Score, 9.9, 10.1);
    }

    [Fact]
    public void meta_weighted_affix_scores_nonzero()  // the un-break
    {
        var w = new StatWeights(new Dictionary<string, double> { ["base_maximum_life"] = 20 }, 100, 85,
            new Dictionary<string, double> { ["base_maximum_life"] = 115 });
        Assert.True(GearScorer.Score(new[] { A("base_maximum_life", 120) }, w).Score > 0);
    }
}
