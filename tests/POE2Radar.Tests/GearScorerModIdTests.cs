using POE2Radar.Core.Gear;
using Xunit;

namespace POE2Radar.Tests;

public class GearScorerModIdTests
{
    [Fact]
    public void modId_flows_into_the_contribution()
    {
        var affixes = new[] { new Affix("line", new[] { "base_maximum_life" }, 100, "IncreasedLife5") };
        var w = new StatWeights(new Dictionary<string, double> { ["base_maximum_life"] = 1 }, 100, 85);
        var s = GearScorer.Score(affixes, w);
        Assert.Equal("IncreasedLife5", s.Affixes[0].ModId);
    }
}
