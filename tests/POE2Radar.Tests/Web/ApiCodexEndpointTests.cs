using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class ApiCodexEndpointTests
{
    [Fact]
    public async Task Missing_character_query_returns_400()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/codex");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Empty_character_query_returns_400()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/codex?character=");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Loopback_request_returns_empty_events_when_provider_unwired()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var http = new HttpClient();
            var resp = await http.GetAsync($"http://localhost:{port}/api/codex?character=Alice");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("\"events\"", body);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("events").ValueKind);
        }
        finally { api.Dispose(); }
    }
}
