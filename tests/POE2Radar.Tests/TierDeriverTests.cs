using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class TierDeriverTests
{
    [Fact]
    public void highest_required_level_is_T1_and_tierCount_is_family_size()
    {
        var mods = new[]
        {
            new TierDeriver.ModKey("IncreasedLife1", "IncreasedLife", "prefix", "item", 1),
            new TierDeriver.ModKey("IncreasedLife5", "IncreasedLife", "prefix", "item", 33),
            new TierDeriver.ModKey("IncreasedLife9", "IncreasedLife", "prefix", "item", 60),
        };
        var d = TierDeriver.Derive(mods);
        Assert.Equal((1, 3), d["IncreasedLife9"]);   // highest level → T1 (best)
        Assert.Equal((3, 3), d["IncreasedLife1"]);   // lowest level → T3
    }

    [Fact]
    public void different_groups_bucket_independently()
    {
        var mods = new[]
        {
            new TierDeriver.ModKey("FireResist1", "FireResist", "suffix", "item", 1),
            new TierDeriver.ModKey("Strength1", "Strength", "suffix", "item", 1),
        };
        var d = TierDeriver.Derive(mods);
        Assert.Equal((1, 1), d["FireResist1"]);
        Assert.Equal((1, 1), d["Strength1"]);
    }
}
