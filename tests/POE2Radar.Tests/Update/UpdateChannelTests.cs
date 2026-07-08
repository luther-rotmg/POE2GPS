using POE2Radar.Core.Update;
using Xunit;

namespace POE2Radar.Tests.Update;

public class UpdateChannelTests
{
    [Fact]
    public void Stable_channel_default_resolves_to_latest_endpoint()
    {
        var url = AutoUpdatePolicy.Resolve(preview: false, urlOverride: null);
        Assert.EndsWith("/releases/latest", url);
    }

    [Fact]
    public void Preview_channel_resolves_to_releases_list()
    {
        var url = AutoUpdatePolicy.Resolve(preview: true, urlOverride: null);
        Assert.EndsWith("/releases", url);
        Assert.DoesNotContain("/latest", url);
    }

    [Fact]
    public void UrlOverride_wins_regardless_of_channel()
    {
        var custom = "https://gitee.com/mirror/POE2GPS/releases";
        Assert.Equal(custom, AutoUpdatePolicy.Resolve(preview: false, urlOverride: custom));
        Assert.Equal(custom, AutoUpdatePolicy.Resolve(preview: true,  urlOverride: custom));
    }

    [Fact]
    public void AssetName_still_uses_release_tag()
    {
        // Regression protection: the tag-name convention (POE2GPS-{tag}-win-x64.zip)
        // is not affected by channel or URL override.
        Assert.Equal("POE2GPS-v0.20.1-win-x64.zip", AutoUpdatePolicy.AssetName("v0.20.1"));
    }
}
