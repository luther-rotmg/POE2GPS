using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Web;

public class StreamSafeSettingsTests
{
    [Fact]
    public void RadarSettings_DefaultsMatchV035Spec()
    {
        var s = new RadarSettings();
        Assert.Equal(30, s.WebObsSafeDelaySec);
        Assert.True(s.WebObsSafeMaskZoneName);
        Assert.True(s.WebObsSafeHideoutBlur);
        Assert.False(s.WebObsSafeEntityNameFog);
    }

    [Fact]
    public void RadarSettings_AllFieldsRoundTripOnDirectAssignment()
    {
        var s = new RadarSettings
        {
            WebObsSafeDelaySec = 15,
            WebObsSafeMaskZoneName = false,
            WebObsSafeHideoutBlur = false,
            WebObsSafeEntityNameFog = true,
        };
        Assert.Equal(15, s.WebObsSafeDelaySec);
        Assert.False(s.WebObsSafeMaskZoneName);
        Assert.False(s.WebObsSafeHideoutBlur);
        Assert.True(s.WebObsSafeEntityNameFog);
    }

    [Fact]
    public async Task ApiSettings_GetExposesAllFourFieldsWithDefaults()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            using var client = new HttpClient();
            var body = await client.GetStringAsync($"http://localhost:{port}/api/settings");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal(30, root.GetProperty("webObsSafeDelaySec").GetInt32());
            Assert.True(root.GetProperty("webObsSafeMaskZoneName").GetBoolean());
            Assert.True(root.GetProperty("webObsSafeHideoutBlur").GetBoolean());
            Assert.False(root.GetProperty("webObsSafeEntityNameFog").GetBoolean());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiSettings_PostAppliesAllFourFieldsAndDelayIsClamped()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            using var client = new HttpClient();
            var payload = "{\"webObsSafeDelaySec\":9999,\"webObsSafeMaskZoneName\":false,\"webObsSafeHideoutBlur\":false,\"webObsSafeEntityNameFog\":true}";
            var post = await client.PostAsync($"http://localhost:{port}/api/settings", new StringContent(payload, Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.OK, post.StatusCode);
            var body = await client.GetStringAsync($"http://localhost:{port}/api/settings");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal(600, root.GetProperty("webObsSafeDelaySec").GetInt32()); // clamped
            Assert.False(root.GetProperty("webObsSafeMaskZoneName").GetBoolean());
            Assert.False(root.GetProperty("webObsSafeHideoutBlur").GetBoolean());
            Assert.True(root.GetProperty("webObsSafeEntityNameFog").GetBoolean());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiSettings_PostRejectsNegativeDelayByClampingToZero()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            using var client = new HttpClient();
            var payload = "{\"webObsSafeDelaySec\":-5}";
            await client.PostAsync($"http://localhost:{port}/api/settings", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await client.GetStringAsync($"http://localhost:{port}/api/settings");
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(0, doc.RootElement.GetProperty("webObsSafeDelaySec").GetInt32());
        }
        finally { api.Dispose(); }
    }
}