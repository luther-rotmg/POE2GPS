namespace POE2Radar.Core.Game;

/// <summary>Pure derivation of a mod's TIER within its family. RePoE has no tier field, so tier is
/// computed: bucket mods on (group, generationType, domain), rank by requiredLevel DESCENDING so T1 = the
/// highest-level / strongest, tierCount = bucket size. Ties broken by mod id for determinism.</summary>
public static class TierDeriver
{
    public readonly record struct ModKey(string ModId, string Group, string GenType, string Domain, int RequiredLevel);

    public static Dictionary<string, (int Tier, int TierCount)> Derive(IEnumerable<ModKey> mods)
    {
        var byBucket = new Dictionary<(string, string, string), List<ModKey>>();
        foreach (var m in mods)
        {
            var key = (m.Group ?? "", m.GenType ?? "", m.Domain ?? "");
            if (!byBucket.TryGetValue(key, out var list)) byBucket[key] = list = new List<ModKey>();
            list.Add(m);
        }
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var bucket in byBucket.Values)
        {
            var ranked = bucket.OrderByDescending(m => m.RequiredLevel).ThenBy(m => m.ModId, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ranked.Count; i++) result[ranked[i].ModId] = (i + 1, ranked.Count);
        }
        return result;
    }
}
