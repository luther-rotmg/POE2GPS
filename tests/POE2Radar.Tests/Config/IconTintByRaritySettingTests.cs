using POE2Radar.Overlay.Config;
using System.Text.Json;
using Xunit;

namespace POE2Radar.Tests.Config;

public class IconTintByRaritySettingTests
{
    [Fact]
    public void DefaultIsTrue()
    {
        var s = new RadarSettings();
        Assert.True(s.IconTintByRarity);
    }

    [Fact]
    public void JsonRoundTripPreservesValue()
    {
        var s = new RadarSettings { IconTintByRarity = false };
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<RadarSettings>(json)!;
        Assert.False(back.IconTintByRarity);
    }

    [Fact]
    public void MissingKeyInJson_LeavesDefault()
    {
        var back = JsonSerializer.Deserialize<RadarSettings>("{}")!;
        Assert.True(back.IconTintByRarity);
    }
}