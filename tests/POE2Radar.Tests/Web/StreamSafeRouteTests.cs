using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class StreamSafeRouteTests
{
    static async Task<(int status, string body)> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync($"http://localhost:{port}{path}");
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Obs_WithoutMode_StillServesLegacyObsBodyClass()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            var (status, body) = await GetAsync(port, "/obs");
            Assert.Equal(200, status);
            Assert.Contains("class=\"obs\"", body);
            Assert.DoesNotContain("safe-mode", body);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Obs_ModeSafe_InjectsSafeModeBodyClassAndAllFourDataAttrs()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            var (status, body) = await GetAsync(port, "/obs?mode=safe");
            Assert.Equal(200, status);
            Assert.Contains("class=\"obs safe-mode\"", body);
            Assert.Contains("data-safe-delay-sec=\"30\"", body);       // default from S1
            Assert.Contains("data-safe-mask-zone=\"1\"", body);         // default true
            Assert.Contains("data-safe-hideout-blur=\"1\"", body);      // default true
            Assert.Contains("data-safe-entity-name-fog=\"0\"", body);   // default false
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Obs_ModeSafe_DelayQueryParamOverridesSettingAndIsClamped()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            var (_, body1) = await GetAsync(port, "/obs?mode=safe&delay=45");
            Assert.Contains("data-safe-delay-sec=\"45\"", body1);
            var (_, body2) = await GetAsync(port, "/obs?mode=safe&delay=9999");
            Assert.Contains("data-safe-delay-sec=\"600\"", body2);
            var (_, body3) = await GetAsync(port, "/obs?mode=safe&delay=-5");
            Assert.Contains("data-safe-delay-sec=\"0\"", body3);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Obs_ModeSafe_IsCaseInsensitive()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            var (_, body) = await GetAsync(port, "/obs?mode=SAFE");
            Assert.Contains("safe-mode", body);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Obs_ModeSafe_Returns404WhenEnableWebObsOff()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port);
        try
        {
            var (status, _) = await GetAsync(port, "/obs?mode=safe");
            Assert.Equal(404, status);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Obs_ModeSafe_EtagDiffersFromLegacyObs()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            using var client = new HttpClient();
            using var r1 = await client.GetAsync($"http://localhost:{port}/obs");
            using var r2 = await client.GetAsync($"http://localhost:{port}/obs?mode=safe");
            var etag1 = r1.Headers.ETag?.Tag;
            var etag2 = r2.Headers.ETag?.Tag;
            Assert.NotNull(etag1);
            Assert.NotNull(etag2);
            Assert.NotEqual(etag1, etag2);
        }
        finally { api.Dispose(); }
    }
}