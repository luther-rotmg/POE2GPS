using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Game;

/// <summary>
/// Reach — CHOR-41 (v0.26): parses the clipboard-copied PoE2 waystone item text into a risk-tiered
/// mod list plus a combo score, so users can decide whether a waystone is worth running before
/// they slot it. Read-only (no clipboard interaction here — the caller passes the text blob in);
/// pure computation; never throws.
///
/// The rule table (patterns + risk tiers + weights) and the combo table (multi-mod danger bonuses)
/// live in embedded <c>poe2_waystone_mod_risk.json</c>. Modify that file to teach the risk model
/// about new mods; the C# loader is tolerant of missing / malformed entries.
/// </summary>
public sealed class WaystoneModRisk
{
    public enum RiskTier { Safe, Notable, Deadly, LethalCombo }

    /// <summary>One matched mod on the waystone. <see cref="Line"/> is the raw text from the
    /// clipboard block; <see cref="ModKey"/> is the rule's internal id (used for combo matching).</summary>
    public readonly record struct WaystoneMod(string Line, string ModKey, string Name, RiskTier Tier, int Weight);

    /// <summary>One triggered combo — 2+ mod keys hit simultaneously.</summary>
    public readonly record struct ComboHit(string Label, int Bonus, IReadOnlyList<string> Keys);

    /// <summary>Full parse result. <see cref="Tier"/> is the shipped waystone tier (0 if not parseable).
    /// <see cref="Rarity"/> comes from the item header. <see cref="TotalScore"/> = sum of mod
    /// weights + combo bonuses. <see cref="ShouldSkip"/> flips true at TotalScore ≥ 60 (rough guideline).</summary>
    public sealed record WaystoneRiskResult(
        bool IsWaystone,
        int Tier,
        string Rarity,
        IReadOnlyList<WaystoneMod> Mods,
        IReadOnlyList<ComboHit> Combos,
        int TotalScore,
        bool ShouldSkip);

    private static readonly Lazy<WaystoneModRisk> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);
    public static WaystoneModRisk Shared => _shared.Value;

    private sealed record CatalogRule(Regex Pattern, string Key, string Name, RiskTier Tier, int Weight);
    private sealed record ComboRule(IReadOnlyList<string> Keys, int Bonus, string Label);

    private readonly CatalogRule[] _rules;
    private readonly ComboRule[] _combos;

    public const int SkipThreshold = 60;

    private WaystoneModRisk(CatalogRule[] rules, ComboRule[] combos)
    {
        _rules = rules;
        _combos = combos;
    }

    public int RuleCount => _rules.Length;
    public int ComboCount => _combos.Length;

    /// <summary>Parse a clipboard blob. Returns <see cref="WaystoneRiskResult"/> with
    /// <c>IsWaystone=false</c> if the text is missing the <c>Item Class: Waystones</c> header
    /// (or the closest fuzzy variant). Empty / non-item text returns an empty result.</summary>
    public WaystoneRiskResult Parse(string clipboardBlob)
    {
        if (string.IsNullOrWhiteSpace(clipboardBlob))
            return new WaystoneRiskResult(false, 0, "", Array.Empty<WaystoneMod>(),
                Array.Empty<ComboHit>(), 0, false);

        // Header check: PoE2 always emits "Item Class: Waystones" as the first section.
        if (!clipboardBlob.Contains("Item Class: Waystones", StringComparison.OrdinalIgnoreCase)
            && !clipboardBlob.Contains("Item Class: Maps", StringComparison.OrdinalIgnoreCase))
        {
            return new WaystoneRiskResult(false, 0, "", Array.Empty<WaystoneMod>(),
                Array.Empty<ComboHit>(), 0, false);
        }

        // Pull the Rarity header if present.
        var rarity = ExtractHeaderValue(clipboardBlob, "Rarity");

        // Pull the shipped tier (best-effort).
        int tier = 0;
        var tierLine = ExtractLineContaining(clipboardBlob, "Waystone Tier:");
        if (tierLine != null && int.TryParse(ExtractNumberAtEnd(tierLine), out var t)) tier = t;

        // Match every mod line against the rule table. First rule that hits wins per line
        // (so a specific "reflect X% of Physical" doesn't also fire "extra damage as fire").
        var mods = new List<WaystoneMod>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in clipboardBlob.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            foreach (var rule in _rules)
            {
                if (rule.Pattern.IsMatch(line))
                {
                    if (seenKeys.Add(rule.Key))
                        mods.Add(new WaystoneMod(line, rule.Key, rule.Name, rule.Tier, rule.Weight));
                    break;
                }
            }
        }

        // Evaluate combo bonuses.
        var combos = new List<ComboHit>();
        foreach (var combo in _combos)
        {
            bool allMatch = true;
            foreach (var k in combo.Keys) if (!seenKeys.Contains(k)) { allMatch = false; break; }
            if (allMatch) combos.Add(new ComboHit(combo.Label, combo.Bonus, combo.Keys));
        }

        int total = 0;
        foreach (var m in mods) total += m.Weight;
        foreach (var c in combos) total += c.Bonus;
        // Sort mods deadliest first, then by weight desc.
        mods.Sort((a, b) =>
        {
            int t = ((int)b.Tier).CompareTo((int)a.Tier);
            return t != 0 ? t : b.Weight.CompareTo(a.Weight);
        });

        return new WaystoneRiskResult(true, tier, rarity, mods, combos, total, total >= SkipThreshold);
    }

    // ── loading ─────────────────────────────────────────────────────────────────────────────────────

    private static WaystoneModRisk LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = OpenResource(asm, "waystone_mod_risk");
            if (stream == null) return Empty();

            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var rules = new List<CatalogRule>();
            if (root.TryGetProperty("rules", out var ra) && ra.ValueKind == JsonValueKind.Array)
                foreach (var el in ra.EnumerateArray())
                {
                    var pat = Str(el, "pattern");
                    var key = Str(el, "key");
                    var name = Str(el, "name");
                    var tier = ParseTier(Str(el, "tier"));
                    var weight = Int(el, "weight");
                    if (pat.Length == 0 || key.Length == 0) continue;
                    Regex regex;
                    try { regex = new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                    catch { continue; }
                    rules.Add(new CatalogRule(regex, key, name, tier, weight));
                }

            var combos = new List<ComboRule>();
            if (root.TryGetProperty("combos", out var ca) && ca.ValueKind == JsonValueKind.Array)
                foreach (var el in ca.EnumerateArray())
                {
                    var keys = new List<string>();
                    if (el.TryGetProperty("keys", out var ka) && ka.ValueKind == JsonValueKind.Array)
                        foreach (var k in ka.EnumerateArray())
                            if (k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } s) keys.Add(s);
                    if (keys.Count < 2) continue;
                    combos.Add(new ComboRule(keys, Int(el, "bonus"), Str(el, "label")));
                }

            return new WaystoneModRisk(rules.ToArray(), combos.ToArray());
        }
        catch
        {
            return Empty();
        }
    }

    private static WaystoneModRisk Empty()
        => new(Array.Empty<CatalogRule>(), Array.Empty<ComboRule>());

    private static Stream? OpenResource(Assembly asm, string contains)
    {
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains(contains));
        return name == null ? null : asm.GetManifestResourceStream(name);
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static RiskTier ParseTier(string s) => s switch
    {
        "Safe" or "safe"           => RiskTier.Safe,
        "Notable" or "notable"     => RiskTier.Notable,
        "Deadly" or "deadly"       => RiskTier.Deadly,
        _                          => RiskTier.Notable,
    };

    private static string ExtractHeaderValue(string blob, string headerName)
    {
        foreach (var raw in blob.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                return line.Substring(headerName.Length + 1).Trim();
        }
        return "";
    }

    private static string? ExtractLineContaining(string blob, string needle)
    {
        foreach (var raw in blob.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) return line;
        }
        return null;
    }

    private static string ExtractNumberAtEnd(string line)
    {
        int i = line.Length - 1;
        while (i >= 0 && !char.IsDigit(line[i])) i--;
        int end = i;
        while (i >= 0 && char.IsDigit(line[i])) i--;
        return end >= 0 && end >= i + 1 ? line.Substring(i + 1, end - i) : "";
    }
}
