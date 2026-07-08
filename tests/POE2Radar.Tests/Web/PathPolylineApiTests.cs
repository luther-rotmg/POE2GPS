using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

// v0.20.1 T9: /api/paths returns the current selected-target route polylines built from
// OverlayRenderer's already-populated SelectedPaths list (no new memory reads). Same OR-gate
// as /api/map + /landmarks so /obs alone can seed its client without enabling /map.
public class PathPolylineApiTests
{
    [Fact]
    public async Task ApiPaths_returns_current_paths_when_web_enabled()
    {
        var state = SseChannelTests.MakeStateWithPaths(new[]
        {
            new[] { (10f, 20f), (30f, 40f) },
            new[] { (5f, 5f), (6f, 6f), (7f, 7f) },
        });
        var api = TestBoot.Server(webMap: true, webObs: false, out var port,
                                  stateProvider: () => state);
        try
        {
            using var client = new HttpClient();
            var text = await client.GetStringAsync($"http://localhost:{port}/api/paths");
            using var doc = JsonDocument.Parse(text);
            var paths = doc.RootElement.GetProperty("paths");
            Assert.Equal(2, paths.GetArrayLength());
            Assert.Equal(2, paths[0].GetProperty("points").GetArrayLength());
            Assert.Equal(3, paths[1].GetProperty("points").GetArrayLength());
            // Spot-check the first point on the first polyline round-trips as { x, y } floats.
            var pt0 = paths[0].GetProperty("points")[0];
            Assert.Equal(10f, pt0.GetProperty("x").GetSingle());
            Assert.Equal(20f, pt0.GetProperty("y").GetSingle());
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ApiPaths_returns_404_when_web_off()
    {
        var api = TestBoot.Server(webMap: false, webObs: false, out var port);
        try
        {
            using var client = new HttpClient();
            var resp = await client.GetAsync($"http://localhost:{port}/api/paths");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { api.Dispose(); }
    }
}
