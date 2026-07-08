using System.Text.Json;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class DeltaEntityTests
{
    [Fact]
    public void First_snapshot_is_full()
    {
        var s = SseChannelTests.MakeState();
        var frame = SseChannel.BuildFrameForTest(s, new EntityDeltaState());
        var doc = JsonDocument.Parse(ExtractJson(frame));
        Assert.True(doc.RootElement.GetProperty("full").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("entities", out _));
        Assert.False(doc.RootElement.TryGetProperty("entitiesDelta", out _));
    }

    [Fact]
    public void Second_snapshot_is_delta()
    {
        var delta = new EntityDeltaState();
        var s1 = SseChannelTests.MakeStateWithEntities(new[]
        {
            (id: 1, x: 10f, y: 20f),
            (id: 2, x: 30f, y: 40f),
        });
        _ = SseChannel.BuildFrameForTest(s1, delta);

        // Move entity 1, remove 2, add 3.
        var s2 = SseChannelTests.MakeStateWithEntities(new[]
        {
            (id: 1, x: 11f, y: 20f),
            (id: 3, x: 50f, y: 60f),
        });
        var frame = SseChannel.BuildFrameForTest(s2, delta);
        var doc = JsonDocument.Parse(ExtractJson(frame));
        Assert.False(doc.RootElement.GetProperty("full").GetBoolean());
        var d = doc.RootElement.GetProperty("entitiesDelta");
        Assert.Single(d.GetProperty("add").EnumerateArray());
        Assert.Single(d.GetProperty("upd").EnumerateArray());
        Assert.Single(d.GetProperty("del").EnumerateArray());
        Assert.Equal(3, d.GetProperty("add")[0].GetProperty("id").GetInt32());
        Assert.Equal(1, d.GetProperty("upd")[0].GetProperty("id").GetInt32());
        Assert.Equal(2, d.GetProperty("del")[0].GetInt32());
    }

    static string ExtractJson(byte[] sseFrame)
    {
        var text = System.Text.Encoding.UTF8.GetString(sseFrame);
        return text.Substring("data: ".Length, text.Length - "data: ".Length - 2);
    }
}
