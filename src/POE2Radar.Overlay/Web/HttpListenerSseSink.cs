using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace POE2Radar.Overlay.Web;

sealed class HttpListenerSseSink : ISseSink
{
    readonly HttpListenerResponse _response;
    int _closed;

    public HttpListenerSseSink(HttpListenerResponse response)
    {
        _response = response;
        _response.ContentType = "text/event-stream";
        _response.Headers["Cache-Control"] = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no"; // nginx hint; no-op on direct HttpListener
        _response.SendChunked = true;
        _response.KeepAlive = true;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (IsClosed) throw new InvalidOperationException("sink closed");
        await _response.OutputStream.WriteAsync(data).ConfigureAwait(false);
        await _response.OutputStream.FlushAsync().ConfigureAwait(false);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _response.OutputStream.Close(); } catch { }
        try { _response.Close(); } catch { }
    }
}
