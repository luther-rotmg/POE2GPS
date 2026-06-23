using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class ModRangesTests
{
    [Fact]
    public void embedded_ranges_load_and_a_known_life_mod_has_range_and_tier()
    {
        Assert.True(ModRanges.Shared.Count > 0);
        // Find any IncreasedLife* mod (the family is guaranteed present in the RePoE export).
        var hit = ModRanges.Shared.TryGet("IncreasedLife5", out var info);
        Assert.True(hit, "expected IncreasedLife5 in the embedded ranges");
        Assert.NotEmpty(info.Stats);
        Assert.True(info.Stats[0].Max >= info.Stats[0].Min);
        Assert.True(info.Tier >= 1 && info.TierCount >= info.Tier);
    }
}
