// tests/POE2Radar.Tests/DynastyMapsTests.cs
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

public class DynastyMapsTests
{
    [Fact]
    public void embedded_table_loads_and_maps_code_to_name_and_gems()
    {
        Assert.True(DynastyMaps.Shared.Count >= 4);
        Assert.True(DynastyMaps.Shared.TryGet("MapVaalVault", out var info), "expected MapVaalVault in the dynasty table");
        Assert.Equal("Sealed Vault", info.Name);
        Assert.NotEmpty(info.Gems);
        Assert.Contains("Atalui's Bloodletting", info.Gems);
    }

    [Fact]
    public void unknown_code_returns_false()
        => Assert.False(DynastyMaps.Shared.TryGet("MapNotADynastyMap", out _));
}
