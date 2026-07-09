using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Core;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using POE2Radar.Tests.Web;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Task 7 PROBE-CONTRIBUTE — covers the trace-file selection rule, the
/// {install_uuid, boot_id, event_count, jsonl_gzip_b64} pack shape, the
/// SiblingContributeUrl "trace" rewrite, and the HTTP-level loopback-Host gate
/// on /api/probe/reset-install-id (audit finding #12 — the install_uuid is the
/// only correlation handle across contributed traces, so the reset MUST NOT be
/// reachable from LAN peers even when AllowLanAccess flips the bind).</summary>
public class ApiServerTraceContributeTests
{
    // Local variant of TestBoot that hands back the RadarSettings reference so the
    // reset-install-id tests can assert the mutation without re-reading from disk.
    static ApiServer BootWithSettings(out int port, out RadarSettings settings)
    {
        port = TestBoot.NextPort();
        settings = new RadarSettings { ApiPort = port, AllowLanAccess = false, ProbeInstallId = "" };
        var api = new ApiServer(
            state: () => SseChannelTests.MakeState(),
            settings: settings,
            navGet: null!, navToggle: null!, navClear: null!,
            hidden: null!, displayRules: null!, landmarkStore: null!,
            tilesProvider: null!, knownModsProvider: null!,
            objectives: null!, seenPoisProvider: null!,
            entityAtlasProvider: null!, entityNames: null!,
            gearProvider: null!, preloadProvider: null!, buffsDiagProvider: null!,
            gearWeights: null!,
            allowLanAccess: false, port: port,
            terrainProvider: () => (null, 0, 0, 0u));
        api.Start();
        return api;
    }

    [Fact]
    public void SelectTraceFileForContribute_prefers_current_when_events_written()
    {
        var got = ApiServer.SelectTraceFileForContribute(
            currentPath: "C:/traces/boot-current.jsonl",
            currentEventCount: 12,
            mostRecentComplete: "C:/traces/boot-old.jsonl");
        Assert.Equal("C:/traces/boot-current.jsonl", got);
    }

    [Fact]
    public void SelectTraceFileForContribute_falls_back_to_most_recent_when_current_empty()
    {
        var got = ApiServer.SelectTraceFileForContribute(
            currentPath: "C:/traces/boot-current.jsonl",
            currentEventCount: 0,
            mostRecentComplete: "C:/traces/boot-old.jsonl");
        Assert.Equal("C:/traces/boot-old.jsonl", got);
    }

    [Fact]
    public void SelectTraceFileForContribute_returns_null_when_nothing_to_share()
    {
        Assert.Null(ApiServer.SelectTraceFileForContribute(null, 0, null));
        Assert.Null(ApiServer.SelectTraceFileForContribute("C:/x.jsonl", 0, null));
    }

    [Fact]
    public void BuildTracePack_emits_snake_case_envelope_with_gzipped_base64_body()
    {
        var install = "11111111-2222-4333-8444-555555555555";
        var boot    = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        var jsonl   = Encoding.UTF8.GetBytes(
            "{\"event_type\":\"zone_entered\",\"area_name\":\"Clearfell\"}\n" +
            "{\"event_type\":\"boss_encountered\",\"boss_display_name\":\"Beira\"}\n");

        var packJson = ApiServer.BuildTracePack(install, boot, eventCount: 2, jsonlBytes: jsonl);

        using var doc = JsonDocument.Parse(packJson);
        var root = doc.RootElement;
        Assert.Equal(install, root.GetProperty("install_uuid").GetString());
        Assert.Equal(boot,    root.GetProperty("boot_id").GetString());
        Assert.Equal(2,       root.GetProperty("event_count").GetInt64());

        var b64 = root.GetProperty("jsonl_gzip_b64").GetString()!;
        var gz  = System.Convert.FromBase64String(b64);
        using var msIn  = new MemoryStream(gz);
        using var gzIn  = new GZipStream(msIn, CompressionMode.Decompress);
        using var msOut = new MemoryStream();
        gzIn.CopyTo(msOut);
        Assert.Equal(jsonl, msOut.ToArray());
    }

    [Fact]
    public void SiblingContributeUrl_rewrites_onto_trace_route()
    {
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev", "trace"));
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev/submit-atlas", "trace"));
        Assert.Equal("https://x.workers.dev/submit-trace",
            ApiServer.SiblingContributeUrl("https://x.workers.dev/submit-buffs/", "trace"));
    }

    // ── /api/probe/reset-install-id — loopback-Host gate (audit finding #12) ──

    [Fact]
    public async Task ResetInstallId_returns_200_and_new_uuid_on_loopback_host()
    {
        var api = BootWithSettings(out var port, out var settings);
        try
        {
            settings.ProbeInstallId = "00000000-0000-4000-8000-000000000000";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/probe/reset-install-id");
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            // Response is serialized with the ApiServer's CamelCase JSON options — the anonymous
            // property `new_install_uuid` therefore appears verbatim (CamelCase only rewrites the
            // leading char). Both spellings are tolerated so a future policy tweak doesn't red-line
            // this integration test.
            var uuidProp = doc.RootElement.TryGetProperty("new_install_uuid", out var p1) ? p1
                          : doc.RootElement.GetProperty("newInstallUuid");
            var minted = uuidProp.GetString();
            Assert.False(string.IsNullOrEmpty(minted));
            Assert.NotEqual("00000000-0000-4000-8000-000000000000", minted);
            Assert.Equal(minted, settings.ProbeInstallId);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ResetInstallId_rejects_non_loopback_host_header()
    {
        // Even though the TCP source is loopback (we're posting to localhost), a spoofed
        // Host header must not succeed. This is the audit-finding-#12 non-negotiable:
        // reset-install-id MUST NOT be reachable via a Host header a LAN peer could set.
        // Defense-in-depth: HttpListener's own strict-Host matcher rejects mismatched Host
        // with 400 before the ApiServer handler runs when the listener is bound to a specific
        // host prefix (loopback-only). The ApiServer's IsLoopbackHost fallback (403) is what
        // catches the same spoof once the AllowLanAccess wildcard bind is on. Either outcome
        // is a rejection — the security invariant is `settings.ProbeInstallId` stays put.
        var api = BootWithSettings(out var port, out var settings);
        try
        {
            settings.ProbeInstallId = "11111111-1111-4111-8111-111111111111";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/probe/reset-install-id");
            req.Headers.Host = "evil.example.com";
            using var resp = await client.SendAsync(req);
            Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
            // Settings must be untouched — the reset never fired.
            Assert.Equal("11111111-1111-4111-8111-111111111111", settings.ProbeInstallId);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ResetInstallId_rejects_get_with_405()
    {
        var api = BootWithSettings(out var port, out var settings);
        try
        {
            settings.ProbeInstallId = "22222222-2222-4222-8222-222222222222";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await client.GetAsync($"http://localhost:{port}/api/probe/reset-install-id");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
            Assert.Equal("22222222-2222-4222-8222-222222222222", settings.ProbeInstallId);
        }
        finally { api.Dispose(); }
    }

    // ── /api/contribute-trace — loopback-Host gate + probe-disabled short-circuit ──

    [Fact]
    public async Task ContributeTrace_rejects_non_loopback_host_header()
    {
        // Mirror of ResetInstallId_rejects_non_loopback_host_header — the invariant is
        // "spoofed Host doesn't succeed"; either HttpListener's platform layer or the
        // ApiServer's IsLoopbackHost gate must catch it.
        var api = BootWithSettings(out var port, out _);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/contribute-trace");
            req.Headers.Host = "evil.example.com";
            using var resp = await client.SendAsync(req);
            Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ContributeTrace_returns_400_when_no_writer_wired()
    {
        // No traceWriter passed to ApiServer ctor -> _traceWriter == null. The handler
        // must 400 with "campaign probe disabled" instead of NPE'ing on FlushSync().
        var api = BootWithSettings(out var port, out var settings);
        try
        {
            settings.EnableCampaignProbe = true;
            settings.ContributeUrl = "https://x.workers.dev";
            settings.ProbeInstallId = "33333333-3333-4333-8333-333333333333";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/contribute-trace");
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("campaign probe disabled", body);
        }
        finally { api.Dispose(); }
    }
}
