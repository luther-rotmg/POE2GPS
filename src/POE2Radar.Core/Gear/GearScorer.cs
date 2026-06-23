namespace POE2Radar.Core.Gear;

/// <summary>One rolled affix on an item: the rendered stat line, the GGG stat ids it maps to (for weight
/// lookup), and the rolled numeric value.</summary>
public sealed record Affix(string StatLine, IReadOnlyList<string> StatIds, double Value);

/// <summary>The user's scoring config: a weight per GGG stat id, the raw total that maps to 100, and the
/// 0–100 score at/above which an item is a "god roll". <paramref name="NormById"/> is the per-stat
/// normalization denominator (e.g. median god-roll value); when present, contribution is
/// <c>(value / norm) × weight</c> instead of <c>value × weight</c>.</summary>
public sealed record StatWeights(
    IReadOnlyDictionary<string, double> ByStatId,
    double Target,
    double GodRollThreshold,
    IReadOnlyDictionary<string, double>? NormById = null);

/// <summary>One affix's contribution to the score (for the dashboard breakdown).</summary>
public sealed record AffixContribution(string Line, IReadOnlyList<string> StatIds, double Value, double Weight, double Points);

/// <summary>The result of scoring one item.</summary>
public sealed record GearScore(double Score, bool IsGodRoll, IReadOnlyList<AffixContribution> Affixes);

/// <summary>
/// Pure 0–100 gear scorer. An item's score is the user-weighted sum of its rolled affix values, scaled so
/// that <see cref="StatWeights.Target"/> maps to 100 and clamped to [0, 100]. Each affix takes the MAX
/// weight among the stat ids it maps to (0 if none are weighted). No game/memory access — it scores
/// already-read affixes, so it is fully unit-testable.
/// </summary>
public static class GearScorer
{
    public static GearScore Score(IReadOnlyList<Affix> affixes, StatWeights weights)
    {
        var target = weights.Target > 0 ? weights.Target : 1.0; // avoid divide-by-zero; degenerate config → high scores
        var contributions = new List<AffixContribution>(affixes.Count);
        double raw = 0;

        foreach (var a in affixes)
        {
            var weight = WeightFor(a.StatIds, weights.ByStatId);
            var norm = NormFor(a.StatIds, weights.NormById);
            var points = a.Value / norm * weight;
            raw += points;
            contributions.Add(new AffixContribution(a.StatLine, a.StatIds, a.Value, weight, points));
        }

        var score = Math.Clamp(raw / target * 100.0, 0.0, 100.0);
        return new GearScore(score, score >= weights.GodRollThreshold, contributions);
    }

    /// <summary>The strongest weight the item assigns to this affix: the max weight over its stat ids
    /// (0 if the item weights none of them).</summary>
    private static double WeightFor(IReadOnlyList<string> statIds, IReadOnlyDictionary<string, double> byStatId)
    {
        double best = 0;
        for (var i = 0; i < statIds.Count; i++)
            if (byStatId.TryGetValue(statIds[i], out var w) && w > best)
                best = w;
        return best;
    }

    /// <summary>The normalization denominator for this affix: the norm of the SAME stat id that supplied the
    /// max weight, or 1 when no norm is configured. Keeps big-number stats (Life ~115) comparable to small
    /// ones (resist ~37).</summary>
    private static double NormFor(IReadOnlyList<string> statIds, IReadOnlyDictionary<string, double>? normById)
    {
        if (normById == null) return 1.0;
        double best = 1.0;
        for (var i = 0; i < statIds.Count; i++)
            if (normById.TryGetValue(statIds[i], out var n) && n > 0)
                return n;   // first configured norm wins (stat ids are positional; one per affix in practice)
        return best;
    }
}
