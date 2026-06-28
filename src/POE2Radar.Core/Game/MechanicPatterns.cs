using System;
using System.Collections.Generic;
using System.Linq;

namespace POE2Radar.Core.Game;

/// <summary>Classifies a PoE2 entity metadata path to a league-mechanic name via the same canonical
/// substrings the display-rule "Mechanics" use. Pure + unit-tested. Directory-qualified terms avoid the
/// bare-"Shrine"/"Strongbox" cosmetic false-positives.</summary>
public static class MechanicPatterns
{
    private static readonly (string Name, string Sub)[] _patterns =
    {
        ("Expedition", "Expedition2/Expedition2Encounter"),
        ("Ritual",     "Ritual"),
        ("Breach",     "Breach"),
        ("Strongbox",  "StrongBoxes"),
        ("Essence",    "Essence"),
        ("Shrine",     "Metadata/Shrines/"),
    };

    /// <summary>Mechanic names in display order.</summary>
    public static IReadOnlyList<string> Names { get; } = _patterns.Select(p => p.Name).ToList();

    /// <summary>The mechanic name for a metadata path, or null if it isn't a known mechanic marker.
    /// Case-insensitive substring match (mirrors DisplayRules mechanic matching).</summary>
    public static string? Classify(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        foreach (var (name, sub) in _patterns)
            if (metadata.Contains(sub, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }
}
