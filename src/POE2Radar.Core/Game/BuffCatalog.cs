using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

public enum BuffTier { Minor = 0, Notable = 1, Deadly = 2 }

public readonly record struct BuffInfo(string Name, BuffTier Tier);
public readonly record struct BuffLine(string Text, BuffTier Tier);

public readonly record struct BuffFilter(
    BuffTier Threshold, IReadOnlySet<string> AlwaysShow, IReadOnlySet<string> Hide, bool DisplayAll, int MaxLines);

/// <summary>Maps monster buff ids (snake_case internal names) to a readable name + danger tier (curated
/// table + substring-heuristic fallback), suppresses engine-junk ids, and selects the display lines for a
/// mob given a filter. Pure (no memory access). Mirrors <see cref="MonsterAffixCatalog"/>.</summary>
public sealed class BuffCatalog
{
    private static readonly Lazy<BuffCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static BuffCatalog Shared => _shared.Value;

    private readonly Dictionary<string, BuffInfo> _curated;
    public IReadOnlyDictionary<string, BuffInfo> Curated => _curated;
    private BuffCatalog(Dictionary<string, BuffInfo> curated) => _curated = curated;

    // Engine/internal buffs that are never player-relevant — suppressed unless DisplayAll (diagnostic).
    private static readonly string[] JunkSubstrings =
        { "_tracker", "should_aim", "_reservation", "presence_events", "_debug", "_internal" };
    private static bool IsJunk(string id)
    {
        foreach (var j in JunkSubstrings) if (id.Contains(j, StringComparison.Ordinal)) return true;
        return false;
    }

    public BuffInfo Resolve(string id)
    {
        if (_curated.TryGetValue(id, out var info)) return info;
        return new BuffInfo(Prettify(id), HeuristicTier(id));
    }

    /// <summary>snake_case internal id → Title Case display name. ("igniting_presence_aura" → "Igniting Presence Aura")</summary>
    public static string Prettify(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p.Substring(1));
        var name = string.Join(' ', parts).Trim();
        return name.Length == 0 ? id : name;
    }

    /// <summary>Best-effort danger tier for an uncatalogued id, from substring signals.</summary>
    public static BuffTier HeuristicTier(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Contains("enrage") || s.Contains("berserk") || s.Contains("frenzied") || s.Contains("empower")) return BuffTier.Deadly;
        if (s.Contains("aura") || s.Contains("shield") || s.Contains("fortif") || s.Contains("speed")
            || s.Contains("haste") || s.Contains("damage") || s.Contains("regen") || s.Contains("resist")) return BuffTier.Notable;
        return BuffTier.Minor;
    }

    public IReadOnlyList<BuffLine> Select(IReadOnlyList<(string Id, float Timer, bool Permanent)> buffs, BuffFilter f)
    {
        var picked = new List<BuffLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in buffs)
        {
            if (f.Hide.Contains(b.Id)) continue;                         // hide overrides everything
            if (IsJunk(b.Id) && !f.DisplayAll && !f.AlwaysShow.Contains(b.Id)) continue;
            var info = Resolve(b.Id);
            bool show = f.DisplayAll || f.AlwaysShow.Contains(b.Id) || info.Tier >= f.Threshold;
            if (!show) continue;
            var text = b.Permanent || b.Timer <= 0f ? info.Name
                : $"{info.Name} {(int)MathF.Ceiling(b.Timer)}s";
            if (!seen.Add(text)) continue;                              // de-dup by display text
            picked.Add(new BuffLine(text, info.Tier));
        }
        picked.Sort((a, b) => a.Tier != b.Tier ? b.Tier.CompareTo(a.Tier) : string.CompareOrdinal(a.Text, b.Text));
        if (picked.Count > f.MaxLines) picked.RemoveRange(f.MaxLines, picked.Count - f.MaxLines);
        return picked;
    }

    private static BuffCatalog LoadEmbedded()
    {
        var curated = new Dictionary<string, BuffInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("poe2_notable_buffs", StringComparison.Ordinal));
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
                        var tier = tierStr switch { "Deadly" => BuffTier.Deadly, "Notable" => BuffTier.Notable, _ => BuffTier.Minor };
                        curated[p.Name] = new BuffInfo(nm, tier);
                    }
                }
            }
        }
        catch { /* empty curated table → everything prettifies + heuristic-tiers */ }
        return new BuffCatalog(curated);
    }
}
