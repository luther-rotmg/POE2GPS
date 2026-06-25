// src/POE2Radar.Core/Game/IslandRumours.cs
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

public sealed record RumourEntry(
    string Rumor,
    string Type,
    string Map,
    string Mods,
    string Tier,
    string Note);

public sealed record RankedRumour(
    RumourEntry Entry,
    bool IsBestPick);

/// <summary>Embedded island-rumour tier table for the Expedition "Uncharted Waters" selection screen.
/// Loaded once from the embedded <c>island_rumours.json</c>. Read-only; pure-function lookup helpers
/// are fully unit-testable (no memory, no Overlay types).</summary>
public sealed class IslandRumours
{
    // Singleton -- loaded once on first access, never throws out of Load().
    public static IslandRumours Shared { get; } = Load();

    // Full table keyed by normalised+lowercased label for O(1) lookup.
    private readonly Dictionary<string, RumourEntry> _byLabel;
    private IslandRumours(Dictionary<string, RumourEntry> byLabel) => _byLabel = byLabel;

    /// <summary>Known font/map-name prefixes that appear at Str1 (textStruct+0x20) in place of the
    /// actual display label. When Str1 starts with any of these, the read recipe falls back to
    /// Str2 (textStruct+0x50). Case-sensitive -- these are exact known prefixes.</summary>
    internal static readonly HashSet<string> KnownMapNames = new(StringComparer.Ordinal)
    {
        "Fontin Smallcaps",
        "OptimusPrincepsSemiBold",
    };

    // -- Tier rank (pure, static) -------------------------------------------

    /// <summary>Maps a tier string to an integer rank for sorting (higher = better).
    /// S+=6, S=5, A=4, B+=3, B=2, C=1, F=0, unknown/null=-1.</summary>
    public static int TierRank(string tier) => tier switch
    {
        "S+" => 6,
        "S"  => 5,
        "A"  => 4,
        "B+" => 3,
        "B"  => 2,
        "C"  => 1,
        "F"  => 0,
        _    => -1,
    };

    // -- Normalisation (private, pure) --------------------------------------

    /// <summary>Single-pass normalisation: trim surrounding whitespace; then strip exactly ONE
    /// trailing suffix -- either the horizontal ellipsis U+2026 ('…') OR three ASCII dots ("..."),
    /// checked in that order via if/else-if (only one suffix removed per call); then trim again.</summary>
    private static string NormaliseLabel(string s)
    {
        s = s.Trim();
        if (s.EndsWith('…'))       // horizontal ellipsis U+2026 -- checked first
            s = s[..^1];
        else if (s.EndsWith("..."))     // three ASCII dots -- only if U+2026 was NOT present
            s = s[..^3];
        return s.Trim();
    }

    // -- Public pure helpers ------------------------------------------------

    /// <summary>Normalise <paramref name="raw"/> and look it up in the table.
    /// Returns null when the string is empty or no entry matches.</summary>
    public RumourEntry? MatchLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var key = NormaliseLabel(raw).ToLowerInvariant();
        return _byLabel.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>For each label in <paramref name="labels"/>, call MatchLabel, drop non-matches and
    /// duplicates (by <c>RumourEntry.Rumor</c>), sort best-first by tier rank, set
    /// <c>IsBestPick = true</c> on index 0. Returns an empty list when nothing matches.</summary>
    public IReadOnlyList<RankedRumour> RankOffered(IEnumerable<string> labels)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<RumourEntry>();
        foreach (var lbl in labels)
        {
            var e = MatchLabel(lbl);
            if (e == null) continue;
            if (!seen.Add(e.Rumor)) continue;  // dedupe by canonical rumor name
            matched.Add(e);
        }
        matched.Sort((a, b) => TierRank(b.Tier).CompareTo(TierRank(a.Tier)));
        var result = new List<RankedRumour>(matched.Count);
        for (int i = 0; i < matched.Count; i++)
            result.Add(new RankedRumour(matched[i], IsBestPick: i == 0));
        return result;
    }

    // -- Loader -------------------------------------------------------------

    private static IslandRumours Load()
    {
        var byLabel = new Dictionary<string, RumourEntry>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.Contains("island_rumours", StringComparison.Ordinal));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                {
                    var list = s != null
                        ? JsonSerializer.Deserialize<List<RumourEntry>>(s,
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        : null;
                    if (list != null)
                        foreach (var e in list)
                        {
                            var key = NormaliseLabel(e.Rumor).ToLowerInvariant();
                            byLabel[key] = e;
                        }
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"IslandRumours load failed: {ex.Message}"); }
        return new IslandRumours(byLabel);
    }
}
