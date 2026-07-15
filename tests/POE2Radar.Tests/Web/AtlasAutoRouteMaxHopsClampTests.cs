using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Web;

public class AtlasAutoRouteMaxHopsClampTests
{
    [Fact]
    public void RadarSettings_default_is_zero_meaning_unlimited()
    {
        var s = new RadarSettings();
        Assert.Equal(0, s.AtlasAutoRouteMaxHops);
    }

    [Fact]
    public async Task Post_zero_round_trips_and_preserves_unlimited_semantics()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            var body = new StringContent("{\"atlasAutoRouteMaxHops\":0}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"http://localhost:{port}/api/settings", body);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            var settings = JsonDocument.Parse(await client.GetStringAsync($"http://localhost:{port}/api/settings"));
            Assert.Equal(0, settings.RootElement.GetProperty("atlasAutoRouteMaxHops").GetInt32());
        }
        finally { api.Dispose(); }
    }

    [Theory]
    [InlineData(-5, 0)]     // negative clamps up to 0 (still unlimited)
    [InlineData(0, 0)]      // 0 preserved
    [InlineData(1, 1)]      // 1 preserved (min meaningful hop cap)
    [InlineData(32, 32)]    // 32 preserved (max)
    [InlineData(100, 32)]   // above 32 clamps down
    public async Task Post_clamps_to_zero_thirtytwo(int input, int expected)
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            var postBody = new StringContent($"{{\"atlasAutoRouteMaxHops\":{input}}}", Encoding.UTF8, "application/json");
            var postResp = await client.PostAsync($"http://localhost:{port}/api/settings", postBody);
            Assert.Equal(System.Net.HttpStatusCode.OK, postResp.StatusCode);

            var settings = JsonDocument.Parse(await client.GetStringAsync($"http://localhost:{port}/api/settings"));
            Assert.Equal(expected, settings.RootElement.GetProperty("atlasAutoRouteMaxHops").GetInt32());
        }
        finally { api.Dispose(); }
    }
}