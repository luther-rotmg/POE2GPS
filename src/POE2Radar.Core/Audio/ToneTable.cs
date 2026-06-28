using System;
using System.Collections.Generic;
using System.Linq;

namespace POE2Radar.Core.Audio;

/// <summary>Named alert tones (frequency + duration), rendered through PureToneWav. The overlay's
/// audio cues pick one per event. Pure + unit-tested.</summary>
public static class ToneTable
{
    public readonly record struct Tone(string Name, double FreqHz, int DurationMs);

    private static readonly Tone[] _tones =
    {
        new("Chime", 660, 120),
        new("Bell",  880, 150),
        new("Ding",  520, 180),
        new("Beep",  880,  90),
        new("Blip",  440,  70),
        new("Alert",1040, 110),
        new("Low",   330, 160),
    };

    public const string Default = "Chime";

    /// <summary>Tone names in display order (for the dashboard select).</summary>
    public static IReadOnlyList<string> Names { get; } = _tones.Select(t => t.Name).ToList();

    /// <summary>Resolve a tone by name (case-insensitive); unknown/empty → the Default tone.</summary>
    public static Tone Get(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            foreach (var t in _tones)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var t in _tones) if (t.Name == Default) return t;
        return _tones[0];
    }

    /// <summary>WAV bytes for a named tone at the given volume (0..1).</summary>
    public static byte[] Wav(string? name, double volume)
    {
        var t = Get(name);
        return PureToneWav.Generate(t.FreqHz, t.DurationMs, volume);
    }
}
