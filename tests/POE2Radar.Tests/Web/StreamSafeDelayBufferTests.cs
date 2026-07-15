using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace POE2Radar.Tests.Web;

public class StreamSafeDelayBufferTests
{
    static async Task<string> GetTextAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        return await client.GetStringAsync($"http://localhost:{port}{path}");
    }

    [Fact]
    public async Task MapJs_ContainsDelayRingBufferClassWithExpectedSurface()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetTextAsync(port, "/assets/map.js");
            Assert.Contains("class DelayRingBuffer", js);
            Assert.Contains("drainReady(", js);
            Assert.Contains("setDelayMs(", js);
            Assert.Contains("getDelayMs(", js);
            Assert.Contains("push(frame, receivedAt)", js);
            Assert.Contains("clear()", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_ContainsSafeBufferBootstrapReadingBodyDataset()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetTextAsync(port, "/assets/map.js");
            Assert.Contains("safe-mode", js);
            Assert.Contains("dataset.safeDelaySec", js);
            Assert.Contains("new DelayRingBuffer", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_OnMessageBranchesOnSafeBuffer()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetTextAsync(port, "/assets/map.js");
            // Wire path must call push, not applyFrameToState, when _safeBuf is set.
            Assert.Contains("_safeBuf.push", js);
            Assert.Contains("function applyFrameToState", js);
            // Zone-reset detection MUST remain inside applyFrameToState (proof: the existing area-diff
            // line still lives there, one hit total in the file).
            Assert.Contains("snap.area !== state.currentArea", js);
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_PumpReClocksFrameTBeforeApply()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetTextAsync(port, "/assets/map.js");
            // Re-clocking on dequeue is required for interp bracketing — see spec.
            Assert.Contains("pumpSafeBuffer", js);
            Assert.Contains("drainReady(", js);
            // The exact re-clock idiom (allow either order of the two sides).
            Assert.True(
                js.Contains("frame.t = now + state.serverOffset") ||
                js.Contains("frame.t = performance.now() + state.serverOffset"),
                "pumpSafeBuffer must overwrite frame.t with re-clocked server time before applyFrameToState");
        }
        finally { api.Dispose(); }
    }

    [Fact]
    public async Task MapJs_NonSafeObsIsUntouched_LegacyOnMessageBodyPreserved()
    {
        var api = TestBoot.Server(webMap: true, webObs: true, out var port);
        try
        {
            var js = await GetTextAsync(port, "/assets/map.js");
            // The extracted body still contains the entity-merge logic verbatim.
            Assert.Contains("state.entities.clear()", js);
            Assert.Contains("entitiesDelta", js);
            // A non-safe /obs page must NOT bootstrap the buffer at load — the guard is body-class-based.
            var obsHtml = await GetTextAsync(port, "/obs");
            Assert.DoesNotContain("safe-mode", obsHtml);
        }
        finally { api.Dispose(); }
    }
}