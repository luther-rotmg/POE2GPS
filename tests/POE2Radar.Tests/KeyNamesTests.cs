using POE2Radar.Core.Input;

namespace POE2Radar.Tests;

public class KeyNamesTests
{
    [Theory]
    [InlineData(0x75, "F6")] [InlineData(0x7B, "F12")] [InlineData(0x70, "F1")]
    [InlineData(0x52, "R")]  [InlineData(0x4D, "M")]
    [InlineData(0x31, "1")]  [InlineData(0x30, "0")]
    [InlineData(0xDD, "]")]  [InlineData(0xDB, "[")]
    public void formats_known_keys(int vk, string expected) => Assert.Equal(expected, KeyNames.Format(vk));

    [Fact] public void unknown_vk_is_hex_fallback() => Assert.Equal("Key01", KeyNames.Format(0x01));
}
