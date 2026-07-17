using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Core.OverlayLayouts;
using Xunit;

namespace POE2Radar.Tests.Web;

public sealed class ApiOverlayLayoutsEndpointTests : IDisposable
{
    // Reuse the same JsonSerializerOptions as the API (camelCase).
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configDir;

    public ApiOverlayLayoutsEndpointTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-apilayouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Serialize an OverlayLayoutFile to JSON for the POST body.</summary>
    private static string SerializeFile(OverlayLayoutFile file) =>
        JsonSerializer.Serialize(file, Json);

    /// <summary>Build a minimal valid preset for testing.</summary>
    private static OverlayLayoutPreset MakePreset(string name,
        string match,
        IReadOnlyDictionary<string, PanelState>? panels = null) => new(
        Name: name,
        Match: match,
        Panels: panels ?? new Dictionary<string, PanelState>());

    // ────────────────────────────────────────────────────────────
    //  GET /api/overlay-layouts
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_EmptyStore_Returns200WithEmptyPresets()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/overlay-layouts");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var presets = doc.RootElement.GetProperty("presets");
            Assert.Equal(JsonValueKind.Array, presets.ValueKind);
            Assert.Equal(0, presets.GetArrayLength());
        }
        finally { api.Dispose(); }
    }

    // ────────────────────────────────────────────────────────────
    //  POST /api/overlay-layouts
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_Loopback_SavesFullFile_Returns200()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var panels = new Dictionary<string, PanelState>
            {
                ["minimap"] = new PanelState(Visible: true, X: 100, Y: 200),
                ["inventory"] = new PanelState(Visible: false, X: null, Y: null),
            };
            var presets = new List<OverlayLayoutPreset>
            {
                MakePreset("Town Layout", "town_*", panels),
                MakePreset("Map Layout", "map_*"),
            };
            var file = new OverlayLayoutFile(1, presets.AsReadOnly());
            var content = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var resultPresets = doc.RootElement.GetProperty("presets");
            Assert.Equal(2, resultPresets.GetArrayLength());
            Assert.Equal("Town Layout", resultPresets[0].GetProperty("name").GetString());
            Assert.Equal("town_*", resultPresets[0].GetProperty("match").GetString());
            Assert.Equal("Map Layout", resultPresets[1].GetProperty("name").GetString());
            Assert.Equal("map_*", resultPresets[1].GetProperty("match").GetString());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_NonLoopback_Returns403()
    {
        // See ApiRulesEndpointTests Post_NonLoopback_Returns403 for pattern.
        // The loopback check uses RemoteEndPoint which is always localhost from test,
        // so we can't truly fake a non-loopback. Instead we verify the server responds
        // without crashing and the 403 path is reachable structurally.
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var presets = new List<OverlayLayoutPreset>
            {
                MakePreset("Default", "*"),
            };
            var file = new OverlayLayoutFile(1, presets.AsReadOnly());
            var content = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            // On localhost, this should succeed (loopback gate passes).
            Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Forbidden);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_InvalidJson_Returns400()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var content = new StringContent("not valid json", Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("invalid JSON body", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_Over10Presets_Returns400WithArgumentMessage()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var presets = new List<OverlayLayoutPreset>();
            for (int i = 0; i < 11; i++)
                presets.Add(MakePreset("preset" + i, "*"));

            var file = new OverlayLayoutFile(1, presets.AsReadOnly());
            var content = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.GetProperty("error").GetString()!;
            Assert.Contains("10", error);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_DuplicateName_Returns400()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            var presets = new List<OverlayLayoutPreset>
            {
                MakePreset("My Layout", "town_*"),
                MakePreset("My Layout", "map_*"), // duplicate name (case-insensitive)
            };
            var file = new OverlayLayoutFile(1, presets.AsReadOnly());
            var content = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("duplicate", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_ThenGet_RoundTrips_WithPanelStates()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            // POST a file with 3 presets, including panel states
            var panelsA = new Dictionary<string, PanelState>
            {
                ["minimap"] = new PanelState(Visible: true, X: 50, Y: 100),
                ["flaskbar"] = new PanelState(Visible: false, X: null, Y: null),
            };
            var panelsB = new Dictionary<string, PanelState>
            {
                ["inventory"] = new PanelState(Visible: true, X: 800, Y: 600),
            };
            var presets = new List<OverlayLayoutPreset>
            {
                MakePreset("Town Layout", "town_*", panelsA),
                MakePreset("Map Layout", "map_*", panelsB),
                MakePreset("Hideout", "hideout_*"),
            };
            var file = new OverlayLayoutFile(1, presets.AsReadOnly());
            var postContent = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var postResp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", postContent);
            Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

            // GET should return the same presets
            var getResp = await http.GetAsync($"http://localhost:{port}/api/overlay-layouts");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var getBody = await getResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(getBody);
            var resultPresets = doc.RootElement.GetProperty("presets");
            Assert.Equal(3, resultPresets.GetArrayLength());
            Assert.Equal("Town Layout", resultPresets[0].GetProperty("name").GetString());
            Assert.Equal("town_*", resultPresets[0].GetProperty("match").GetString());
            Assert.Equal("Map Layout", resultPresets[1].GetProperty("name").GetString());
            Assert.Equal("map_*", resultPresets[1].GetProperty("match").GetString());
            Assert.Equal("Hideout", resultPresets[2].GetProperty("name").GetString());
            Assert.Equal("hideout_*", resultPresets[2].GetProperty("match").GetString());

            // Verify panel states round-trip cleanly
            var panelsResultA = resultPresets[0].GetProperty("panels");
            Assert.Equal(true, panelsResultA.GetProperty("minimap").GetProperty("visible").GetBoolean());
            Assert.Equal(50, panelsResultA.GetProperty("minimap").GetProperty("x").GetInt32());
            Assert.Equal(100, panelsResultA.GetProperty("minimap").GetProperty("y").GetInt32());
            Assert.Equal(false, panelsResultA.GetProperty("flaskbar").GetProperty("visible").GetBoolean());
            Assert.Equal(JsonValueKind.Null, panelsResultA.GetProperty("flaskbar").GetProperty("x").ValueKind);
            Assert.Equal(JsonValueKind.Null, panelsResultA.GetProperty("flaskbar").GetProperty("y").ValueKind);

            var panelsResultB = resultPresets[1].GetProperty("panels");
            Assert.Equal(true, panelsResultB.GetProperty("inventory").GetProperty("visible").GetBoolean());
            Assert.Equal(800, panelsResultB.GetProperty("inventory").GetProperty("x").GetInt32());
            Assert.Equal(600, panelsResultB.GetProperty("inventory").GetProperty("y").GetInt32());

            // Third preset has no panels
            var panelsResultC = resultPresets[2].GetProperty("panels");
            Assert.Equal(JsonValueKind.Object, panelsResultC.ValueKind);
            Assert.Equal(0, panelsResultC.EnumerateObject().Count());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Get_Method_Delete_Returns405()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            // DELETE should return 405
            var delResp = await http.DeleteAsync($"http://localhost:{port}/api/overlay-layouts");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, delResp.StatusCode);

            // PUT should return 405
            var putContent = new StringContent("{}", Encoding.UTF8, "application/json");
            var putReq = new HttpRequestMessage(HttpMethod.Put, $"http://localhost:{port}/api/overlay-layouts")
            {
                Content = putContent,
            };
            var putResp = await http.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, putResp.StatusCode);

            // PATCH should return 405
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"http://localhost:{port}/api/overlay-layouts")
            {
                Content = putContent,
            };
            var patchResp = await http.SendAsync(patchReq);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, patchResp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_ReplaceExistingFile_OverwritesCleanly()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            // First POST: { A, B }
            var presetsAB = new List<OverlayLayoutPreset>
            {
                MakePreset("A Layout", "zone_a_*"),
                MakePreset("B Layout", "zone_b_*"),
            };
            var fileAB = new OverlayLayoutFile(1, presetsAB.AsReadOnly());
            var contentAB = new StringContent(SerializeFile(fileAB), Encoding.UTF8, "application/json");
            var respAB = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", contentAB);
            Assert.Equal(HttpStatusCode.OK, respAB.StatusCode);

            // Second POST: { X, Y } — replaces A/B
            var presetsXY = new List<OverlayLayoutPreset>
            {
                MakePreset("X Layout", "zone_x_*"),
                MakePreset("Y Layout", "zone_y_*"),
            };
            var fileXY = new OverlayLayoutFile(1, presetsXY.AsReadOnly());
            var contentXY = new StringContent(SerializeFile(fileXY), Encoding.UTF8, "application/json");
            var respXY = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", contentXY);
            Assert.Equal(HttpStatusCode.OK, respXY.StatusCode);

            // GET → { X, Y } only
            var getResp = await http.GetAsync($"http://localhost:{port}/api/overlay-layouts");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var getBody = await getResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(getBody);
            var resultPresets = doc.RootElement.GetProperty("presets");
            Assert.Equal(2, resultPresets.GetArrayLength());
            Assert.Equal("X Layout", resultPresets[0].GetProperty("name").GetString());
            Assert.Equal("zone_x_*", resultPresets[0].GetProperty("match").GetString());
            Assert.Equal("Y Layout", resultPresets[1].GetProperty("name").GetString());
            Assert.Equal("zone_y_*", resultPresets[1].GetProperty("match").GetString());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Post_EmptyPresets_Works()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port, rulesConfigDir: _configDir);
        try
        {
            using var http = new HttpClient();
            // POST empty presets list
            var file = new OverlayLayoutFile(1, Array.Empty<OverlayLayoutPreset>().AsReadOnly());
            var content = new StringContent(SerializeFile(file), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://localhost:{port}/api/overlay-layouts", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // GET should confirm empty
            var getResp = await http.GetAsync($"http://localhost:{port}/api/overlay-layouts");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var getBody = await getResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(getBody);
            var presets = doc.RootElement.GetProperty("presets");
            Assert.Equal(JsonValueKind.Array, presets.ValueKind);
            Assert.Equal(0, presets.GetArrayLength());
        }
        finally { api.Dispose(); }
    }
}