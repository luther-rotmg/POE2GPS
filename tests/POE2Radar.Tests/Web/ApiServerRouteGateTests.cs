using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

// v0.20.0 T5: verifies the browser-view feature gates on the ApiServer dispatch.
// With both toggles off, every one of the six routes (/map /obs /stream /api/map
// /api/atlas /landmarks) plus the /assets/* prefix must 404 without touching the
// null _sse / _assetHost fields. With only /obs on, the data endpoints that
// map.js consumes (/api/map, /api/atlas, /landmarks) must remain registered so
// the shared renderer works — that's the review-flagged bug being fixed here.
public class ApiServerRouteGateTests
{
    static int _portCounter = 43000;
    static int NextPort() => Interlocked.Increment(ref _portCounter);

    static ApiServer BootServer(bool webMap, bool webObs, out int port)
    {
        port = NextPort();
        var settings = new RadarSettings
        {
            EnableWebMap = webMap,
            EnableWebObs = webObs,
            ApiPort = port,
            AllowLanAccess = false,
        };
        var stateProvider = () => (RadarState)default!;
        var sse = (webMap || webObs) ? new SseChannel() : null;
        var host = (webMap || webObs) ? new AssetHost() : null;

        // Named args keep this stable if callback ordering shifts; only the
        // gated routes are exercised so null!-ing the callbacks is safe.
        var api = new ApiServer(
            state: stateProvider,
            settings: settings,
            navGet: null!,
            navToggle: null!,
            navClear: null!,
            hidden: null!,
            displayRules: null!,
            landmarkStore: null!,
            tilesProvider: null!,
            knownModsProvider: null!,
            objectives: null!,
            seenPoisProvider: null!,
            entityAtlasProvider: null!,
            entityNames: null!,
            gearProvider: null!,
            preloadProvider: null!,
            buffsDiagProvider: null!,
            gearWeights: null!,
            allowLanAccess: false,
            port: port,
            sse: sse,
            assetHost: host);
        api.Start();
        return api;
    }

    static async Task<int> StatusAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync($"http://localhost:{port}{path}");
        return (int)resp.StatusCode;
    }

    // /stream is an open-ended SSE stream — reading the full body would hang.
    // ResponseHeadersRead lets us observe the status code and then abort.
    static async Task<int> StreamStatusAsync(int port)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync(
            $"http://localhost:{port}/stream",
            HttpCompletionOption.ResponseHeadersRead);
        return (int)resp.StatusCode;
    }

    [Fact]
    public async Task Both_off_all_six_routes_return_404()
    {
        var api = BootServer(webMap: false, webObs: false, out var port);
        try
        {
            Assert.Equal(404, await StatusAsync(port, "/map"));
            Assert.Equal(404, await StatusAsync(port, "/obs"));
            Assert.Equal(404, await StatusAsync(port, "/stream"));
            Assert.Equal(404, await StatusAsync(port, "/api/map"));
            Assert.Equal(404, await StatusAsync(port, "/api/atlas"));
            Assert.Equal(404, await StatusAsync(port, "/landmarks"));
            Assert.Equal(404, await StatusAsync(port, "/assets/map.css"));
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Only_map_on_obs_is_404_others_ok()
    {
        var api = BootServer(webMap: true, webObs: false, out var port);
        try
        {
            Assert.Equal(200, await StatusAsync(port, "/map"));
            Assert.Equal(404, await StatusAsync(port, "/obs"));
            Assert.Equal(200, await StatusAsync(port, "/assets/map.css"));
            // /stream returns 200 but stays open — tolerate any 2xx here.
            var stream = await StreamStatusAsync(port);
            Assert.InRange(stream, 200, 299);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Only_obs_on_map_is_404_data_endpoints_still_registered()
    {
        var api = BootServer(webMap: false, webObs: true, out var port);
        try
        {
            Assert.Equal(200, await StatusAsync(port, "/obs"));
            Assert.Equal(404, await StatusAsync(port, "/map"));
            // Critical: /api/map, /api/atlas, /landmarks MUST still be registered
            // so /obs's renderer (map.js) can fetch terrain + atlas + landmarks.
            Assert.NotEqual(404, await StatusAsync(port, "/api/map"));
            Assert.NotEqual(404, await StatusAsync(port, "/api/atlas"));
            Assert.NotEqual(404, await StatusAsync(port, "/landmarks"));
        }
        finally { api.Dispose(); }
    }
}
