using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

// v0.21.1: verifies the /api/map wire contract map.js relies on for the
// terrain-not-ready race fix.
//
// The race: on first zone entry the browser's onZoneChange fires before the
// world-thread's terrain callback has this zone's walkable bitmap. map.js
// polls /api/map and expects a symmetric {ready:bool} sentinel:
//
//   { "ready": false }
//       -> not loaded yet, retry in 250ms
//
//   { "ready": true, "areaHash": u32, "width": int, "height": int, "walkable": base64 }
//       -> real payload, build the interior/edges/fog canvases
//
// If either shape drifts, the client retry loop hangs or gives up permanently
// and the tester sees the "path polyline visible, no terrain" symptom the
// Discord report caught.
public class ApiServerTerrainReadyRaceTests
{
    static async Task<string> GetJsonBodyAsync(int port)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        var resp = await client.GetAsync($"http://localhost:{port}/api/map");
        Assert.True(resp.IsSuccessStatusCode,
            $"/api/map returned {(int)resp.StatusCode}; expected 200 for either sentinel or payload");
        return await resp.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ApiMap_returns_ready_false_when_terrain_provider_returns_null()
    {
        // Provider says "no terrain loaded yet."
        System.Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>
            provider = () => (null, 0, 0, 0u);

        var api = TestBoot.Server(webMap: true, webObs: false, out var port,
                                   terrainProvider: provider);
        try
        {
            var body = await GetJsonBodyAsync(port);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("ready", out var ready),
                $"payload missing 'ready' field — map.js retry loop keys on this. Got: {body}");
            Assert.False(ready.GetBoolean(),
                $"expected ready:false when provider returned null. Got: {body}");
            Assert.False(doc.RootElement.TryGetProperty("areaHash", out _),
                "not-ready sentinel must NOT include areaHash — map.js would try to build from garbage");
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiMap_returns_ready_true_with_full_payload_when_provider_returns_terrain()
    {
        // Provider serves a real 8x8 all-walkable grid.
        var walkable = new byte[64];
        System.Array.Fill(walkable, (byte)1);
        const uint expectedHash = 0xDEADBEEFu;
        System.Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>
            provider = () => (walkable, 8, 8, expectedHash);

        var api = TestBoot.Server(webMap: true, webObs: false, out var port,
                                   terrainProvider: provider);
        try
        {
            var body = await GetJsonBodyAsync(port);
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean(),
                $"expected ready:true on the loaded payload. Got: {body}");
            Assert.Equal(expectedHash, doc.RootElement.GetProperty("areaHash").GetUInt32());
            Assert.Equal(8, doc.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(8, doc.RootElement.GetProperty("height").GetInt32());
            var b64 = doc.RootElement.GetProperty("walkable").GetString();
            Assert.NotNull(b64);
            var decoded = System.Convert.FromBase64String(b64!);
            Assert.Equal(64, decoded.Length);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiMap_transitions_from_not_ready_to_ready_across_calls()
    {
        // Provider returns null for the first two GETs, then real terrain on
        // the third — emulating the race window map.js's retry loop closes.
        int calls = 0;
        var walkable = new byte[16];
        System.Array.Fill(walkable, (byte)1);
        System.Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)> provider =
            () =>
            {
                var n = System.Threading.Interlocked.Increment(ref calls);
                return n < 3 ? (null, 0, 0, 0u) : (walkable, 4, 4, 0x11223344u);
            };

        var api = TestBoot.Server(webMap: true, webObs: false, out var port,
                                   terrainProvider: provider);
        try
        {
            var body1 = await GetJsonBodyAsync(port);
            var body2 = await GetJsonBodyAsync(port);
            var body3 = await GetJsonBodyAsync(port);

            using var d1 = JsonDocument.Parse(body1);
            using var d2 = JsonDocument.Parse(body2);
            using var d3 = JsonDocument.Parse(body3);

            Assert.False(d1.RootElement.GetProperty("ready").GetBoolean(),
                $"call 1 should be ready:false. Got: {body1}");
            Assert.False(d2.RootElement.GetProperty("ready").GetBoolean(),
                $"call 2 should be ready:false. Got: {body2}");
            Assert.True(d3.RootElement.GetProperty("ready").GetBoolean(),
                $"call 3 should transition to ready:true. Got: {body3}");
            Assert.Equal(0x11223344u, d3.RootElement.GetProperty("areaHash").GetUInt32());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiMap_ready_false_sentinel_body_is_exact_string()
    {
        // Guards against a maintainer accidentally growing the sentinel shape
        // (e.g. adding a null areaHash field for symmetry); map.js explicitly
        // predicates its retry on the exact `data.ready === false` shape and
        // fetchTerrain returns null on any other unexpected structure.
        System.Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>
            provider = () => (null, 0, 0, 0u);

        var api = TestBoot.Server(webMap: true, webObs: false, out var port,
                                   terrainProvider: provider);
        try
        {
            var body = await GetJsonBodyAsync(port);
            Assert.Equal("{\"ready\":false}", body);
        }
        finally { api.Dispose(); }
    }
}
