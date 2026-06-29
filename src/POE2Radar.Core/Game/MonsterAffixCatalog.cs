using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Game;

public enum AffixTier { Minor = 0, Notable = 1, Deadly = 2 }

public readonly record struct AffixInfo(string Name, AffixTier Tier);
public readonly record struct AffixLine(string Name, AffixTier Tier);

public readonly record struct AffixFilter(
    AffixTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines);

/// <summary>Maps monster affix mod ids to a readable name + danger tier (curated table + auto-prettify
/// fallback), and selects the display lines for a mob given a filter. Pure (no memory access).</summary>
public sealed class MonsterAffixCatalog
{
    private static readonly Lazy<MonsterAffixCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static MonsterAffixCatalog Shared => _shared.Value;

    private readonly Dictionary<string, AffixInfo> _curated;
    public IReadOnlyDictionary<string, AffixInfo> Curated => _curated;

    private MonsterAffixCatalog(Dictionary<string, AffixInfo> curated) => _curated = curated;

    private static readonly Regex SplitBoundary =
        new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])", RegexOptions.Compiled);

    public AffixInfo Resolve(string modId)
    {
        if (_curated.TryGetValue(modId, out var info)) return info;
        return new AffixInfo(Prettify(modId), AffixTier.Minor);
    }

    /// <summary>Strip a leading "Monster" prefix, split camelCase/digit boundaries, title-case.</summary>
    public static string Prettify(string modId)
    {
        var s = modId.StartsWith("Monster", StringComparison.Ordinal) ? modId.Substring(7) : modId;
        if (s.Length == 0) return modId;
        var parts = SplitBoundary.Split(s).Where(p => p.Length > 0);
        var titled = parts.Select(p => p.Length == 1 ? p.ToUpperInvariant()
            : char.ToUpperInvariant(p[0]) + p.Substring(1));
        var name = string.Join(' ', titled).Trim();
        return name.Length == 0 ? modId : name;
    }

    public IReadOnlyList<AffixLine> Select(IReadOnlyList<string> mobModIds, AffixFilter f)
    {
        var picked = new List<AffixLine>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in mobModIds)
        {
            if (f.Hide.Contains(id)) continue;                 // hide overrides everything
            var info = Resolve(id);
            bool show = f.DisplayAll || f.AlwaysShow.Contains(id) || info.Tier >= f.Threshold;
            if (!show) continue;
            if (!seenNames.Add(info.Name)) continue;           // de-dup by display name
            picked.Add(new AffixLine(info.Name, info.Tier));
        }
        picked.Sort((a, b) => a.Tier != b.Tier
            ? b.Tier.CompareTo(a.Tier)                          // Deadly → Minor
            : string.CompareOrdinal(a.Name, b.Name));
        if (picked.Count > f.MaxLines) picked.RemoveRange(f.MaxLines, picked.Count - f.MaxLines);
        return picked;
    }

    private static MonsterAffixCatalog LoadEmbedded()
    {
        var curated = new Dictionary<string, AffixInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("poe2_monster_mod_names", StringComparison.Ordinal));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null)
                {
                    using var doc = JsonDocument.Parse(s);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var nm = p.Value.TryGetProperty("name", out var n2) ? n2.GetString() ?? p.Name : p.Name;
                        var tierStr = p.Value.TryGetProperty("tier", out var t2) ? t2.GetString() : "Minor";
                        var tier = tierStr switch { "Deadly" => AffixTier.Deadly, "Notable" => AffixTier.Notable, _ => AffixTier.Minor };
                        curated[p.Name] = new AffixInfo(nm, tier);
                    }
                }
            }
        }
        catch { /* fall back to empty curated table; everything prettifies */ }
        return new MonsterAffixCatalog(curated);
    }
}
