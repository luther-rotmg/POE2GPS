using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Core.Health;
using POE2Radar.Overlay.Overlay;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class SseChannelTests
{
    // Simple in-memory sink. Records every WriteAsync call.
    sealed class RecordingSink : ISseSink
    {
        public readonly List<byte[]> Frames = new();
        public bool ShouldFault;
        public int WriteDelayMs;
        public bool Closed;

        public bool IsClosed => Closed;

        public async Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            if (Closed) throw new InvalidOperationException("closed");
            if (WriteDelayMs > 0) await Task.Delay(WriteDelayMs);
            if (ShouldFault) throw new System.IO.IOException("fault");
            Frames.Add(data.ToArray());
        }

        public void Close() => Closed = true;
    }

    static RadarState MakeState(uint areaHash = 0xABC123)
        => new RadarState(
            InGame: true,
            AreaHash: areaHash,
            AreaLevel: 1,
            MapVisible: true,
            Zoom: 1f,
            Player: System.Numerics.Vector2.Zero,
            Entities: Array.Empty<Poe2Live.EntityDot>(),
            Landmarks: Array.Empty<Poe2Live.Landmark>(),
            HpPct: 100f,
            ManaPct: 100f,
            EsPct: 100f,
            AreaCode: "test",
            CharName: "TestChar",
            CharLevel: 1,
            WorldMs: 0,
            RenderMs: 0,
            Monoliths: null,
            Director: null,
            Fps: 0,
            Session: null,
            Health: HealthState.Ok,
            HealthMessage: "ok",
            CampaignGps: null,
            RpmPerSec: 0);

    [Fact]
    public void New_channel_is_empty()
    {
        using var c = new SseChannel();
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public async Task Publish_on_empty_channel_writes_nothing()
    {
        using var c = new SseChannel();
        c.Publish(MakeState()); // no exception; nothing to observe
        await Task.Yield();
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public async Task Publish_fans_out_to_all_subscribers()
    {
        using var c = new SseChannel();
        var a = new RecordingSink();
        var b = new RecordingSink();
        c.AddSubscriber(a);
        c.AddSubscriber(b);

        c.Publish(MakeState());
        await WaitFor(() => a.Frames.Count == 1 && b.Frames.Count == 1, 500);

        Assert.Single(a.Frames);
        Assert.Single(b.Frames);
        var text = Encoding.UTF8.GetString(a.Frames[0]);
        Assert.StartsWith("data: ", text);
        Assert.EndsWith("\n\n", text);
    }

    static async Task WaitFor(Func<bool> predicate, int timeoutMs)
    {
        var start = Environment.TickCount;
        while (!predicate() && Environment.TickCount - start < timeoutMs)
            await Task.Delay(5);
    }

    [Fact]
    public async Task Publish_drops_frames_when_subscriber_backs_up()
    {
        using var c = new SseChannel();
        var slow = new RecordingSink { WriteDelayMs = 300 };
        c.AddSubscriber(slow);

        // Fire 10 publishes back to back. First few enqueue; rest exceed MaxQueued and drop.
        for (var i = 0; i < 10; i++) c.Publish(MakeState());

        await Task.Delay(50);
        // At this point at least one frame is in-flight, ≤ MaxQueued queued, and the rest were dropped.
        Assert.True(slow.Frames.Count <= SseChannel.MaxQueued + 1);
    }

    [Fact]
    public async Task Publish_boots_slow_subscriber_after_MaxConsecutiveDrops()
    {
        using var c = new SseChannel();
        var slow = new RecordingSink { WriteDelayMs = 10_000 }; // effectively stuck
        c.AddSubscriber(slow);

        for (var i = 0; i < SseChannel.MaxConsecutiveDrops + SseChannel.MaxQueued + 5; i++)
            c.Publish(MakeState());

        await WaitFor(() => slow.IsClosed || c.IsEmpty, 1000);
        Assert.True(c.IsEmpty, "channel should have booted the stuck subscriber");
        Assert.True(slow.IsClosed);
    }

    [Fact]
    public async Task Publish_removes_faulted_subscriber_leaves_others_intact()
    {
        using var c = new SseChannel();
        var good = new RecordingSink();
        var bad = new RecordingSink { ShouldFault = true };
        c.AddSubscriber(good);
        c.AddSubscriber(bad);

        c.Publish(MakeState());
        c.Publish(MakeState());
        await WaitFor(() => good.Frames.Count >= 2 && bad.IsClosed, 500);

        Assert.True(good.Frames.Count >= 2);
        Assert.True(bad.IsClosed);
    }
}
