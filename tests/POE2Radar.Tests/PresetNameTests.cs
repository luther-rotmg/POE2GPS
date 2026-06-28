using POE2Radar.Core.Config;
using Xunit;

public class PresetNameTests
{
    [Theory]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("..\\..\\win", "win")]
    [InlineData("a/b/c", "abc")]
    [InlineData("name:with*bad?chars", "namewithbadchars")]
    public void strips_path_separators_and_specials(string raw, string expected) => Assert.Equal(expected, PresetName.Sanitize(raw));

    [Theory] [InlineData("")] [InlineData(null)] [InlineData("///")] [InlineData("***")]
    public void empty_after_sanitize_falls_back(string? raw) => Assert.Equal(PresetName.Fallback, PresetName.Sanitize(raw));

    [Fact] public void caps_length() => Assert.True(PresetName.Sanitize(new string('a', 200)).Length <= PresetName.MaxLength);

    [Fact] public void keeps_safe_chars_and_collapses_whitespace() => Assert.Equal("My Preset-2_v", PresetName.Sanitize("  My   Preset-2_v  "));

    [Fact] public void idempotent() { var once = PresetName.Sanitize("a/b c!"); Assert.Equal(once, PresetName.Sanitize(once)); }
}
