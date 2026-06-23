// tests/POE2Radar.Tests/StarterWeightsTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class StarterWeightsTests
{
    [Fact]
    public void embedded_starter_weights_are_nonempty_and_include_life()
    {
        Assert.NotEmpty(StarterWeights.ByStatId);
        Assert.True(StarterWeights.ByStatId.TryGetValue("base_maximum_life", out var w) && w > 0,
            "expected a positive starter weight for base_maximum_life");
        Assert.True(StarterWeights.NormById.TryGetValue("base_maximum_life", out var n) && n > 0,
            "expected a positive norm for base_maximum_life");
    }
}
