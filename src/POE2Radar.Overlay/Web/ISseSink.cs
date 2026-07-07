using System;
using System.Threading.Tasks;

namespace POE2Radar.Overlay.Web;

/// <summary>Testability seam over an SSE wire. Concrete impl wraps HttpListenerResponse; tests use a recording mock.</summary>
public interface ISseSink
{
    Task WriteAsync(ReadOnlyMemory<byte> data);
    void Close();
    bool IsClosed { get; }
}
