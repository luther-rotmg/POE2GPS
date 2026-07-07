using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Overlay.Web;
using Xunit;

// PeriodicTimer uses Windows multimedia timer for drift-free cadence.
using PeriodicTimer = System.Threading.PeriodicTimer;

namespace POE2Radar.Tests.Web;

public class SseIntegrationTests
{
    [Fact]
    public async Task Stream_delivers_close_to_30Hz_over_3s()
    {
        using var sse = new SseChannel();
        var sink = new CountingSink();
        sse.AddSubscriber(sink);

        // Simulate the world thread — publish every ~33 ms for 3 s.
        // PeriodicTimer matches the plan's 30Hz cadence without drift.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var start = DateTime.UtcNow;
        var span = TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow - start < span)
        {
            sse.Publish(SseChannelTests.MakeState());
            if (!await timer.WaitForNextTickAsync()) break;
        }
        // Give writes a moment to drain.
        await Task.Delay(200);

        var count = sink.WriteCount;
        Assert.InRange(count, 81, 99);
    }

    [Fact(Skip = "long — 60s")]
    public async Task Stream_delivers_close_to_30Hz_over_60s_sustained()
    {
        using var sse = new SseChannel();
        var sink = new CountingSink();
        sse.AddSubscriber(sink);

        // PeriodicTimer matches the plan's 30Hz cadence without drift.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var start = DateTime.UtcNow;
        var span = TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow - start < span)
        {
            sse.Publish(SseChannelTests.MakeState());
            if (!await timer.WaitForNextTickAsync()) break;
        }
        await Task.Delay(500);
        Assert.InRange(sink.WriteCount, 1620, 1980);
    }

    [Fact]
    public async Task Stream_multi_subscriber_each_gets_30Hz()
    {
        using var sse = new SseChannel();
        var sinks = new CountingSink[4];
        for (var i = 0; i < 4; i++) { sinks[i] = new CountingSink(); sse.AddSubscriber(sinks[i]); }

        // PeriodicTimer matches the plan's 30Hz cadence without drift.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var start = DateTime.UtcNow;
        var span = TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow - start < span)
        {
            sse.Publish(SseChannelTests.MakeState());
            if (!await timer.WaitForNextTickAsync()) break;
        }
        await Task.Delay(200);

        foreach (var s in sinks) Assert.InRange(s.WriteCount, 81, 99);
    }

    sealed class CountingSink : ISseSink
    {
        int _count;
        public int WriteCount => Volatile.Read(ref _count);
        public bool IsClosed => false;
        public Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
        public void Close() { }
    }
}
