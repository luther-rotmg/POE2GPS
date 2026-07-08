using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class ApiServerGzipTests
{
    [Fact]
    public async Task ApiMap_returns_gzipped_body_when_client_accepts_gzip()
    {
        // Provide a minimal valid terrain payload (10x10 grid all walkable)
        var walkable = new byte[100];
        System.Array.Fill(walkable, (byte)1);
        Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)> terrainProvider =
            () => (walkable, 10, 10, 0x12345678u);

        var api = TestBoot.Server(webMap: true, webObs: false, out var port, terrainProvider: terrainProvider);
        try
        {
            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/map");
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            var resp = await client.SendAsync(req);
            Assert.Equal("gzip", resp.Content.Headers.ContentEncoding.ToString());

            using var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await gz.CopyToAsync(ms);
            var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            Assert.Contains("\"areaHash\"", text);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task Stream_is_not_gzipped_even_with_accept_encoding_gzip()
    {
        var api = TestBoot.Server(webMap: true, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/stream");
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            using var cts = new System.Threading.CancellationTokenSource(200);
            try { await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token); }
            catch (System.OperationCanceledException) { /* expected: /stream keeps open */ }

            // Send a fresh request just to inspect headers, then abort.
            var req2 = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/stream");
            req2.Headers.AcceptEncoding.ParseAdd("gzip");
            using var cts2 = new System.Threading.CancellationTokenSource(200);
            var resp = await client.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, cts2.Token);
            Assert.False(resp.Content.Headers.ContentEncoding.Contains("gzip"));
            resp.Dispose();
        }
        finally { api.Dispose(); }
    }
}
