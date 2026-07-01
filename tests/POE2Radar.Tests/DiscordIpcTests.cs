using System.Text;
using POE2Radar.Core.Presence;

public class DiscordIpcTests
{
    [Fact] public void EncodeFrame_has_le_opcode_and_length_header()
    {
        var frame = DiscordIpc.EncodeFrame(1, "{}");
        Assert.Equal(8 + 2, frame.Length);
        Assert.Equal(1, BitConverter.ToInt32(frame, 0));    // opcode LE
        Assert.Equal(2, BitConverter.ToInt32(frame, 4));    // payload length LE
        Assert.Equal("{}", Encoding.UTF8.GetString(frame, 8, 2));
    }
    [Fact] public void EncodeFrame_utf8_payload_length_is_byte_count_not_char_count()
    {
        var json = "{\"s\":\"éé\"}";              // 2 two-byte chars
        var frame = DiscordIpc.EncodeFrame(0, json);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), BitConverter.ToInt32(frame, 4));
    }
    // NOTE: we deliberately do NOT assert TryConnect(<real-looking id>) here — its result depends on
    // whether Discord is running on the test machine (true if it is, false if not), which makes such a
    // test non-deterministic. We assert only the environment-independent behavior instead.
    [Fact] public void TryConnect_empty_or_whitespace_client_id_returns_false()
    {
        using var ipc = new DiscordIpc();
        Assert.False(ipc.TryConnect(""));      // empty → early-out false, never touches a pipe
        Assert.False(ipc.TryConnect("   "));   // whitespace → false
        Assert.False(ipc.Connected);
    }
    [Fact] public void SetActivity_and_Clear_when_not_connected_are_noops_and_never_throw()
    {
        using var ipc = new DiscordIpc();
        ipc.SetActivity("details", "state", 1234567890, null, null);   // not connected → no-op
        ipc.Clear();
        ipc.Dispose();
        Assert.False(ipc.Connected);
    }
}
