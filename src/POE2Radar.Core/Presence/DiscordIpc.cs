using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace POE2Radar.Core.Presence;

/// <summary>Minimal Discord Rich Presence over the local IPC named pipe (\\.\pipe\discord-ipc-0..9).
/// Frame = int32 opcode (LE) + int32 length (LE) + UTF-8 JSON. Handshake op 0 {v:1,client_id}; update
/// op 1 SET_ACTIVITY. Read-only w.r.t. the game; publishes ONLY to the local Discord client, opt-in.
/// Never throws out of the public surface — a missing/closed Discord degrades to "not connected".</summary>
public sealed class DiscordIpc : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private int _nonce;
    public bool Connected => _pipe is { IsConnected: true };

    public static byte[] EncodeFrame(int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);         // little-endian on x64/Windows
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    /// <summary>Connect to the first available discord-ipc pipe + handshake. Returns false (no throw) if
    /// Discord isn't running or the client id is empty.</summary>
    public bool TryConnect(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        Dispose();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(200);   // 200ms per candidate
                var hello = JsonSerializer.Serialize(new { v = 1, client_id = clientId });
                var frame = EncodeFrame(0, hello);
                pipe.Write(frame, 0, frame.Length);
                DrainOne(pipe);      // read the READY response (best-effort)
                _pipe = pipe;
                return true;
            }
            catch { /* try next pipe index */ }
        }
        return false;
    }

    /// <summary>Push a SET_ACTIVITY. No-op if not connected; on write failure, drops the connection so the
    /// caller can reconnect next cycle.</summary>
    public void SetActivity(string details, string state, long? startUnixSec, string? largeImage, string? largeText)
    {
        if (_pipe is not { IsConnected: true } pipe) return;
        object? timestamps = startUnixSec is { } t ? new { start = t } : null;
        object? assets = largeImage != null ? new { large_image = largeImage, large_text = largeText ?? "" } : null;
        var activity = new { details = Trim(details), state = Trim(state), timestamps, assets };
        var msg = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = (++_nonce).ToString(),
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        try { var f = EncodeFrame(1, msg); pipe.Write(f, 0, f.Length); DrainOne(pipe); }
        catch { Dispose(); }
    }

    public void Clear()
    {
        if (_pipe is not { IsConnected: true } pipe) return;
        var msg = JsonSerializer.Serialize(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = (++_nonce).ToString() });
        try { var f = EncodeFrame(1, msg); pipe.Write(f, 0, f.Length); } catch { }
    }

    private static string? Trim(string? s) => string.IsNullOrEmpty(s) ? null : (s.Length > 128 ? s.Substring(0, 128) : s);

    private static void DrainOne(NamedPipeClientStream pipe)
    {
        // Best-effort read of one response frame (8-byte header + payload) so the pipe buffer doesn't stall.
        // Uses ReadExactly to satisfy CA2022 (exact-read requirement); the whole method is wrapped in try/catch
        // so a short read or any I/O failure is silently swallowed — drain is advisory only.
        try
        {
            var hdr = new byte[8];
            pipe.ReadExactly(hdr, 0, 8);
            var len = BitConverter.ToInt32(hdr, 4);
            if (len is > 0 and < 65536)
            {
                var buf = new byte[len];
                pipe.ReadExactly(buf, 0, len);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }
}
