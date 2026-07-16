using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class ApiUserIconsRouteTests
{
    static async Task<(int status, string body)> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync($"http://localhost:{port}{path}");
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UserIcons_ReturnsJsonArrayWithExpectedShape()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            var (status, body) = await GetAsync(port, "/api/user-icons");
            Assert.Equal(200, status);
            Assert.StartsWith("[", body);
            var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            // If non-empty, verify each element has the expected shape
            if (doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                Assert.True(first.TryGetProperty("name", out _));
                Assert.True(first.TryGetProperty("category", out _));
                Assert.True(first.TryGetProperty("rarity", out _));
                Assert.True(first.TryGetProperty("metadataGlob", out _));
                Assert.True(first.TryGetProperty("dataUri", out var dataUri));
                Assert.StartsWith("data:image/png;base64,", dataUri.GetString());
            }
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task UserIcons_EmitsSnapshotVersionedEtag()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            using var resp = await client.GetAsync($"http://localhost:{port}/api/user-icons");
            Assert.True(resp.Headers.TryGetValues("ETag", out var etags));
            var etag = Assert.Single(etags);
            Assert.Matches("^\"sha1-user-icons-v\\d+\"$", etag);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task UserIcons_MatchingIfNoneMatchReturns304()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            using var firstResp = await client.GetAsync($"http://localhost:{port}/api/user-icons");
            Assert.True(firstResp.Headers.TryGetValues("ETag", out var etags));
            var etag = Assert.Single(etags);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/user-icons");
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var resp = await client.SendAsync(req);
            Assert.Equal(304, (int)resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Empty(body);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task UserIcons_MismatchedIfNoneMatchReturns200()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/user-icons");
            req.Headers.TryAddWithoutValidation("If-None-Match", "\"sha1-user-icons-vBOGUS\"");
            using var resp = await client.SendAsync(req);
            Assert.Equal(200, (int)resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.NotEmpty(body);
            Assert.StartsWith("[", body);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task UserIcons_ContentTypeIsApplicationJson()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            using var resp = await client.GetAsync($"http://localhost:{port}/api/user-icons");
            Assert.Contains("application/json", resp.Content.Headers.ContentType?.MediaType ?? "");
        }
        finally { api.Dispose(); }
    }
}