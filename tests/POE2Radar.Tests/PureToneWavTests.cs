using System.Text;
using POE2Radar.Core.Audio;
using Xunit;

public class PureToneWavTests
{
    static string Ascii(byte[] b, int o, int n) => Encoding.ASCII.GetString(b, o, n);

    [Fact]
    public void header_is_riff_wave_with_fmt_and_data()
    {
        var w = PureToneWav.Generate(440, 100);
        Assert.Equal("RIFF", Ascii(w, 0, 4));
        Assert.Equal("WAVE", Ascii(w, 8, 4));
        Assert.Equal("fmt ", Ascii(w, 12, 4));
        Assert.Equal("data", Ascii(w, 36, 4));
    }

    [Fact]
    public void sample_count_matches_duration()
    {
        var w = PureToneWav.Generate(440, 100, 0.5, 44100);
        int expectedSamples = 44100 * 100 / 1000;
        Assert.Equal(44 + expectedSamples * 2, w.Length);
    }

    [Fact]
    public void peak_amplitude_bounded_by_volume()
    {
        var w = PureToneWav.Generate(440, 200, 0.5);
        short peak = 0;
        for (int o = 44; o + 1 < w.Length; o += 2)
        {
            short v = (short)(w[o] | (w[o + 1] << 8));
            if (System.Math.Abs((int)v) > System.Math.Abs((int)peak)) peak = v;
        }
        Assert.True(System.Math.Abs((int)peak) <= (int)(0.5 * short.MaxValue) + 2);
        Assert.True(System.Math.Abs((int)peak) > 0);   // a 440 Hz tone is audible
    }

    [Fact]
    public void zero_freq_is_silence()
    {
        var w = PureToneWav.Generate(0, 50);
        for (int o = 44; o + 1 < w.Length; o += 2)
            Assert.Equal(0, (short)(w[o] | (w[o + 1] << 8)));
    }
}
