using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Core;

public class Poe2LiveHideoutTests
{
    [Theory]
    [InlineData("MapHideoutCanal_Claimable", true)]
    [InlineData("MapHideoutFarmlands_Claimable", true)]
    [InlineData("MapHideoutFelled_Claimable", true)]
    [InlineData("MapHideoutLimestone_Claimable", true)]
    [InlineData("MapHideoutPrison_Claimable", true)]
    [InlineData("MapHideoutShoreline_Claimable", true)]
    [InlineData("MapHideoutShrine_Claimable", true)]
    [InlineData("G1_town", false)]
    [InlineData("Map1_1", false)]
    [InlineData("MapUberBoss_Doryani", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_hideout_area_codes(string? code, bool expected)
    {
        Assert.Equal(expected, Poe2Live.IsHideoutAreaCode(code));
    }
}