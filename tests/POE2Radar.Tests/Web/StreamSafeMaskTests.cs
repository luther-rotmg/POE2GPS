using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class StreamSafeMaskTests
{
    static async Task<string> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        return await client.GetStringAsync($"http://localhost:{port}{path}");
    }

    [Fact]
    public async Task MapJs_HasZoneNameHelperAndHideoutBlurHelper()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetAsync(port, "/assets/map.js");
            Assert.Contains("function zoneDisplayName", js);
            Assert.Contains("function maybeBlurHideoutPose", js);
            Assert.Contains("snap.isHideout", js);
            Assert.Contains("'<area>'", js); // masked literal
            Assert.Contains("_safeMaskZone", js);
            Assert.Contains("_safeHideoutBlur", js);
            Assert.Contains("state.isHideout", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapCss_HasSafeModeSelectors()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var css = await GetAsync(port, "/assets/map.css");
            Assert.Contains("body.safe-mode", css);
            Assert.Contains(".zone-label-masked", css);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_ZoneMaskGatedOnDatasetAttrNotJustBodyClass()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetAsync(port, "/assets/map.js");
            // Bootstrap must read dataset.safeMaskZone and dataset.safeHideoutBlur so a user can
            // flip either individually via /api/settings without touching mode=safe.
            Assert.Contains("dataset.safeMaskZone", js);
            Assert.Contains("dataset.safeHideoutBlur", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_HideoutBlurSnapsToZeroZeroInGridCoords()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetAsync(port, "/assets/map.js");
            // Contract from spec: zone-center = (0, 0) in Grid coords when blurring.
            Assert.True(
                js.Contains("x: 0, y: 0") || js.Contains("{ x:0, y:0 }") || js.Contains("x:0,y:0"),
                "maybeBlurHideoutPose must return zone-center as x:0, y:0 when blurring");
        }
        finally { api.Dispose(); }
    }
}
