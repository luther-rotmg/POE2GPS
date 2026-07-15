using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class StreamSafeEntityFogTests
{
    static async Task<string> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        return await client.GetStringAsync($"http://localhost:{port}{path}");
    }

    [Fact]
    public async Task MapJs_HasFogNameHelperAndBootstrap()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetAsync(port, "/assets/map.js");
            Assert.Contains("_safeEntityNameFog", js);
            Assert.Contains("dataset.safeEntityNameFog", js);
            Assert.Contains("function fogName", js);
            Assert.Contains("'???'", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_MonolithBestNameRoutedThroughFogName()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetAsync(port, "/assets/map.js");
            // Every .bestName read must land in fogName(...) or a wrapper — allow either idiom.
            Assert.Contains("fogName(", js);
            // A weaker structural check: bestName should appear inside a fogName(...) argument at least once.
            Assert.Matches(new System.Text.RegularExpressions.Regex(@"fogName\([^)]*bestName[^)]*\)"), js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapCss_HasEntityNameFogRule()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var css = await GetAsync(port, "/assets/map.css");
            Assert.Contains("data-safe-entity-name-fog=\"1\"", css);
            Assert.Contains("blur(", css);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task ServedObsSafeHtmlHasFogOffByDefault()
    {
        var api = TestBoot.Server(webMap: false, webObs: true, out var port);
        try
        {
            var html = await GetAsync(port, "/obs?mode=safe");
            Assert.Contains("data-safe-entity-name-fog=\"0\"", html);
        }
        finally { api.Dispose(); }
    }
}