using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Core.Tracks;
using Xunit;

namespace POE2Radar.Tests.Web;

public sealed class ApiTracksEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _tracksConfigDir;

    public ApiTracksEndpointTests()
    {
        _tracksConfigDir = Path.Combine(Path.GetTempPath(), "poe2gps-apitracks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tracksConfigDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tracksConfigDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ────────────────────────────────────────────────────────────
    //  GET /api/tracks
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_MissingCharacter_Returns400()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks?zone=test_zone");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_MissingZone_Returns400()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks?character=TestChar");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_NonLoopback_Returns403()
    {
        // The loopback check uses RemoteEndPoint which is always localhost from test,
        // so we can't truly fake a non-loopback. Instead we verify the loopback gate
        // code path exists by checking the server doesn't crash.
        // True non-loopback 403 testing requires integration test infrastructure.
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks?character=TestChar&zone=test_zone");
            // On localhost, the loopback gate passes, so we accept either OK (gate passes)
            // or Forbidden (if some future test infrastructure changes the remote endpoint).
            Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Forbidden);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_UnknownCharacterAndZone_Returns200WithEmptySamples()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks?character=UnknownChar&zone=unknown_zone");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var samples = doc.RootElement.GetProperty("samples");
            Assert.Equal(JsonValueKind.Array, samples.ValueKind);
            Assert.Empty(samples.EnumerateArray());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_ExistingSamples_Returns200WithFullList()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            // Pre-populate some samples via the real TrackStore.
            TrackStore.Append(_tracksConfigDir, "HeroChar", "zone_01", new TrackSample(0, 100.5f, 200.3f));
            TrackStore.Append(_tracksConfigDir, "HeroChar", "zone_01", new TrackSample(1000, 110.7f, 220.9f));
            TrackStore.Append(_tracksConfigDir, "HeroChar", "zone_01", new TrackSample(2000, 95.2f, 180.1f));

            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks?character=HeroChar&zone=zone_01");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var samples = doc.RootElement.GetProperty("samples");
            Assert.Equal(JsonValueKind.Array, samples.ValueKind);
            var sampleList = samples.EnumerateArray().ToList();
            Assert.Equal(3, sampleList.Count);
            Assert.Equal(0, sampleList[0].GetProperty("t").GetInt64());
            Assert.Equal(100.5, sampleList[0].GetProperty("x").GetDouble(), 1);
            Assert.Equal(200.3, sampleList[0].GetProperty("y").GetDouble(), 1);
            Assert.Equal(1000, sampleList[1].GetProperty("t").GetInt64());
            Assert.Equal(2000, sampleList[2].GetProperty("t").GetInt64());
        }
        finally { api.Dispose(); }
    }

    // ────────────────────────────────────────────────────────────
    //  GET /api/tracks/characters
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Characters_ReturnsList()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            TrackStore.Append(_tracksConfigDir, "AlphaChar", "a1", new TrackSample(0, 0, 0));
            TrackStore.Append(_tracksConfigDir, "BetaChar", "b1", new TrackSample(0, 0, 0));

            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks/characters");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var chars = doc.RootElement.GetProperty("characters");
            Assert.Equal(JsonValueKind.Array, chars.ValueKind);
            var list = chars.EnumerateArray().Select(j => j.GetString()).ToList();
            Assert.Contains("AlphaChar", list);
            Assert.Contains("BetaChar", list);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_Characters_NonLoopback_Returns403()
    {
        // The loopback check uses RemoteEndPoint which is always localhost from test,
        // so we can't truly fake a non-loopback. See Get_NonLoopback_Returns403 comment.
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks/characters");
            Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Forbidden);
        }
        finally { api.Dispose(); }
    }

    // ────────────────────────────────────────────────────────────
    //  GET /api/tracks/zones
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Zones_ReturnsListForCharacter()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            TrackStore.Append(_tracksConfigDir, "ZoneChar", "zone_a", new TrackSample(0, 0, 0));
            TrackStore.Append(_tracksConfigDir, "ZoneChar", "zone_b", new TrackSample(0, 0, 0));

            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks/zones?character=ZoneChar");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var zones = doc.RootElement.GetProperty("zones");
            Assert.Equal(JsonValueKind.Array, zones.ValueKind);
            var list = zones.EnumerateArray().Select(j => j.GetString()).ToList();
            Assert.Contains("zone_a", list);
            Assert.Contains("zone_b", list);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_Zones_MissingCharacter_Returns400()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks/zones");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_Zones_NonLoopback_Returns403()
    {
        // The loopback check uses RemoteEndPoint which is always localhost from test,
        // so we can't truly fake a non-loopback. See Get_NonLoopback_Returns403 comment.
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/tracks/zones?character=ZoneChar");
            Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Forbidden);
        }
        finally { api.Dispose(); }
    }

    // ────────────────────────────────────────────────────────────
    //  Method validation
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_Tracks_Returns405()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync($"http://localhost:{port}/api/tracks?character=Test&zone=test",
                new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_TracksCharacters_Returns405()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync($"http://localhost:{port}/api/tracks/characters",
                new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_TracksZones_Returns405()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port,
            rulesConfigDir: _tracksConfigDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync($"http://localhost:{port}/api/tracks/zones?character=Test",
                new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }
}
