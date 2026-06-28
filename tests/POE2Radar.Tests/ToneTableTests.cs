using System.Text;
using POE2Radar.Core.Audio;
using Xunit;

public class ToneTableTests
{
    [Fact] public void names_include_the_event_defaults()
    { Assert.Contains("Chime", ToneTable.Names); Assert.Contains("Bell", ToneTable.Names); Assert.Contains("Ding", ToneTable.Names); }

    [Fact] public void get_resolves_case_insensitive() => Assert.Equal("Bell", ToneTable.Get("bell").Name);

    [Theory] [InlineData("nonsense")] [InlineData(null)] [InlineData("  ")]
    public void get_unknown_falls_back_to_default(string? n) => Assert.Equal(ToneTable.Default, ToneTable.Get(n).Name);

    [Fact] public void wav_is_valid_and_non_silent()
    {
        var w = ToneTable.Wav("Chime", 0.7);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(w, 0, 4));
        bool nonzero = false;
        for (int o = 44; o + 1 < w.Length; o += 2) if ((short)(w[o] | (w[o + 1] << 8)) != 0) { nonzero = true; break; }
        Assert.True(nonzero);
    }
}
