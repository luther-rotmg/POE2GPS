using POE2Radar.Core.Config;
using Xunit;

public class PresetCodecTests
{
    [Fact] public void round_trips_json()
    {
        var json = "{\"name\":\"test\",\"styles\":{\"a\":1}}";
        Assert.True(PresetCodec.TryDecode(PresetCodec.Encode(json), out var back));
        Assert.Equal(json, back);
    }
    [Fact] public void encode_has_prefix() => Assert.StartsWith(PresetCodec.Prefix, PresetCodec.Encode("{}"));
    [Fact] public void decode_tolerates_missing_prefix()
    {
        var code = PresetCodec.Encode("{\"x\":1}");
        var noPrefix = code.Substring(PresetCodec.Prefix.Length);
        Assert.True(PresetCodec.TryDecode(noPrefix, out var back));
        Assert.Equal("{\"x\":1}", back);
    }
    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("not a code")] [InlineData("POE2GPS-@@@")]
    public void rejects_garbage(string bad) => Assert.False(PresetCodec.TryDecode(bad, out _));
}
