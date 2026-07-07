using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Web;

public class RadarSettingsWebViewToggleTests
{
    [Fact]
    public void EnableWebMap_defaults_to_false()
    {
        var s = new RadarSettings();
        Assert.False(s.EnableWebMap);
    }

    [Fact]
    public void EnableWebObs_defaults_to_false()
    {
        var s = new RadarSettings();
        Assert.False(s.EnableWebObs);
    }

    [Fact]
    public void MissingJsonKeys_deserialize_to_false()
    {
        // Simulate an upgrade from v0.19.6 where neither key is present in radar_settings.json.
        var json = "{\"AllowLanAccess\":true}";
        var s = System.Text.Json.JsonSerializer.Deserialize<RadarSettings>(json)!;
        Assert.False(s.EnableWebMap);
        Assert.False(s.EnableWebObs);
        Assert.True(s.AllowLanAccess); // sanity — other fields still deserialize
    }
}
