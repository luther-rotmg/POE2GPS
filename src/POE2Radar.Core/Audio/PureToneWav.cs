namespace POE2Radar.Core.Audio;

/// <summary>Pure 16-bit mono PCM WAV tone generator (no Windows API). Sine wave with a short linear
/// attack/decay envelope (~8 ms) to avoid clicks. Consumed by the overlay's audio-alert cues.</summary>
public static class PureToneWav
{
    /// <summary>Generate a complete WAV (44-byte RIFF/WAVE header + PCM samples). freqHz &lt;= 0 yields a
    /// valid silent WAV of the requested duration.</summary>
    public static byte[] Generate(double freqHz, int durationMs, double volume = 0.5, int sampleRate = 44100)
    {
        if (durationMs < 0) durationMs = 0;
        if (sampleRate < 8000) sampleRate = 8000;
        volume = volume < 0 ? 0 : volume > 1 ? 1 : volume;
        int sampleCount = (int)((long)sampleRate * durationMs / 1000);
        int dataBytes = sampleCount * 2;
        int fileSize = 44 + dataBytes;
        var buf = new byte[fileSize];

        WriteAscii(buf, 0, "RIFF");
        WriteInt32(buf, 4, fileSize - 8);
        WriteAscii(buf, 8, "WAVE");
        WriteAscii(buf, 12, "fmt ");
        WriteInt32(buf, 16, 16);
        WriteInt16(buf, 20, 1);                 // PCM
        WriteInt16(buf, 22, 1);                 // mono
        WriteInt32(buf, 24, sampleRate);
        WriteInt32(buf, 28, sampleRate * 2);    // byte rate
        WriteInt16(buf, 32, 2);                 // block align
        WriteInt16(buf, 34, 16);                // bits/sample
        WriteAscii(buf, 36, "data");
        WriteInt32(buf, 40, dataBytes);

        int env = System.Math.Min(sampleCount / 2, sampleRate * 8 / 1000);
        double amp = volume * short.MaxValue;
        for (int i = 0; i < sampleCount; i++)
        {
            double e = 1.0;
            if (env > 0)
            {
                if (i < env) e = (double)i / env;
                else if (i >= sampleCount - env) e = (double)(sampleCount - i) / env;
            }
            double s = freqHz > 0 ? System.Math.Sin(2 * System.Math.PI * freqHz * i / sampleRate) : 0;
            WriteInt16(buf, 44 + i * 2, (short)(s * amp * e));
        }
        return buf;
    }

    static void WriteAscii(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
    static void WriteInt16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void WriteInt32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
