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
    [Fact] public void TryConnect_returns_false_when_discord_absent_and_never_throws()
    {
        using var ipc = new DiscordIpc();
        // In CI/dev there is no Discord pipe; must return false, not throw.
        var ok = ipc.TryConnect("000000000000000000");
        Assert.False(ok);
    }
}
