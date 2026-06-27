using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class ZoneOrderProgressTests
{
    private const string Json = """
    [
      { "zone": "Z1", "act": 1, "name": "Zone One",   "next": "Z2", "exitHint": "Zone Two" },
      { "zone": "Z2", "act": 1, "name": "Zone Two",   "next": "Z3", "exitHint": "Zone Three" },
      { "zone": "Z3", "act": 2, "name": "Zone Three", "next": null, "exitHint": null }
    ]
    """;
    private static ZoneOrderProgress P() => new(CampaignRoute.FromJson(Json));

    [Fact] public void Current_zone_on_path_becomes_the_target()
    {
        var p = P();
        Assert.Equal("Z2", p.CurrentStep("Z2").Zone);
    }

    [Fact] public void Latch_advances_forward_only_and_does_not_rewind_on_backtrack()
    {
        var p = P();
        Assert.Equal("Z2", p.CurrentStep("Z2").Zone);   // advance to Z2
        Assert.Equal("Z2", p.CurrentStep("Z1").Zone);   // backtrack to Z1 → target stays the furthest (Z2)
    }

    [Fact] public void Off_path_zone_returns_the_latched_target()
    {
        var p = P();
        p.CurrentStep("Z2");                            // latch at Z2
        Assert.Equal("Z2", p.CurrentStep("SideZoneX").Zone);  // unknown zone → latched target
    }

    [Fact] public void Initial_target_is_the_first_step()
    {
        Assert.Equal("Z1", P().CurrentStep("UnknownStart").Zone);
    }
}
