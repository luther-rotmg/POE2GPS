using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Web;

public class SseIntegrationTests
{
    [Fact]
    public async Task Stream_delivers_close_to_30Hz_over_3s()
    {
        using var sse = new SseChannel();
        var sink = new CountingSink();
        sse.AddSubscriber(sink);

        // Simulate the world thread — publish every ~30 ms for 3 s.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            sse.Publish(SseChannelTests.MakeState());
            await Task.Delay(30);
        }
        // Give writes a moment to drain.
        await Task.Delay(200);

        var count = sink.WriteCount;
        Assert.InRange(count, 60, 99);
    }

    [Fact(Skip = "long — 60s")]
    public async Task Stream_delivers_close_to_30Hz_over_60s_sustained()
    {
        using var sse = new SseChannel();
        var sink = new CountingSink();
        sse.AddSubscriber(sink);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            sse.Publish(SseChannelTests.MakeState());
            await Task.Delay(30);
        }
        await Task.Delay(500);
        Assert.InRange(sink.WriteCount, 1200, 1980);
    }

    [Fact]
    public async Task Stream_multi_subscriber_each_gets_30Hz()
    {
        using var sse = new SseChannel();
        var sinks = new CountingSink[4];
        for (var i = 0; i < 4; i++) { sinks[i] = new CountingSink(); sse.AddSubscriber(sinks[i]); }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            sse.Publish(SseChannelTests.MakeState());
            await Task.Delay(30);
        }
        await Task.Delay(200);

        foreach (var s in sinks) Assert.InRange(s.WriteCount, 60, 99);
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
