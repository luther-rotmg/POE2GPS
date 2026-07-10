using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>
/// Classifies entity metadata paths as known league-mechanic content using an embedded rule table.
/// The noise gate (<see cref="Match"/>) returns null for paths that do not start with one of the
/// <c>gateRoots</c> prefixes, avoiding false positives from art/model/shader asset paths.
/// Loaded once from the embedded <c>preload_catalog.json</c>; never throws.
/// </summary>
public sealed class PreloadCatalog
{
    /// <summary>Describes a catalog rule that matched a preloaded entity path.</summary>
    /// <param name="SpawnEntityMetadata">
    /// Signal — SIG-PRELOAD-CATALOG (v0.23): substring the spawn-detection scan (in RadarApp.WorldTick)
    /// looks for on live entity metadata to decide whether the preloaded content has appeared in the
    /// world. Null = spawn detection disabled for this rule (Shrines, Chests, Rituals — tile-scoped
    /// content where a spawned entity does not mean "encounter resolved"). Non-null = the preload
    /// row hides from the panel once any entity carries this substring in its Metadata.
    /// </param>
    public sealed record CatalogHit(string Label, string Tier, string Category, string Color, string Match, string? SpawnEntityMetadata);

    private static readonly Lazy<PreloadCatalog> _shared =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared singleton, loaded once from the embedded JSON.</summary>
    public static PreloadCatalog Shared => _shared.Value;

    private readonly CatalogRule[] _rules;
    private readonly string[] _gateRoots;

    /// <summary>Number of rules loaded (0 if the JSON failed to parse).</summary>
    public int RuleCount => _rules.Length;

    private PreloadCatalog(CatalogRule[] rules, string[] gateRoots)
    {
        _rules = rules;
        _gateRoots = gateRoots;
    }

    /// <summary>
    /// Matches a lowercase entity metadata path against the catalog.
    /// Returns null if the path does not start with any gateRoot prefix (noise gate),
    /// or if no rule's <c>match</c> substring is found in the path.
    /// The strongest-tier matching rule wins (pinnacle &gt; high &gt; mechanic &gt; interactable).
    /// </summary>
    public CatalogHit? Match(string lowerPath)
    {
        if (string.IsNullOrEmpty(lowerPath)) return null;

        // Noise gate: path must start with one of the known entity-metadata roots.
        bool gated = false;
        foreach (var root in _gateRoots)
            if (lowerPath.StartsWith(root, StringComparison.Ordinal)) { gated = true; break; }
        if (!gated) return null;

        // Rules are pre-sorted pinnacle → high → mechanic → interactable; first substring match wins.
        foreach (var rule in _rules)
            if (lowerPath.Contains(rule.Match, StringComparison.Ordinal))
                return new CatalogHit(rule.Label, rule.Tier, rule.Category, rule.Color, rule.Match, rule.SpawnEntityMetadata);

        return null;
    }

    // ── loading ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly int[] TierRanks = new int[4]; // keyed by enum value

    private enum Tier { Interactable = 0, Mechanic = 1, High = 2, Pinnacle = 3 }

    private static int TierRank(string? tier) => tier switch
    {
        "pinnacle"    => (int)Tier.Pinnacle,
        "high"        => (int)Tier.High,
        "mechanic"    => (int)Tier.Mechanic,
        "interactable"=> (int)Tier.Interactable,
        _             => (int)Tier.Interactable
    };

    private sealed record CatalogRule(string Match, string Label, string Tier, string Category, string Color, string? SpawnEntityMetadata);

    private static PreloadCatalog LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = OpenResource(asm, "preload_catalog");
            if (stream == null) return Empty();

            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            // Parse gateRoots
            var gateRoots = new List<string>();
            if (root.TryGetProperty("gateRoots", out var gr) && gr.ValueKind == JsonValueKind.Array)
                foreach (var el in gr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                        gateRoots.Add(s);

            // Parse rules
            var rules = new List<CatalogRule>();
            if (root.TryGetProperty("rules", out var ra) && ra.ValueKind == JsonValueKind.Array)
                foreach (var el in ra.EnumerateArray())
                {
                    var match    = Str(el, "match");
                    var label    = Str(el, "label");
                    var tier     = Str(el, "tier");
                    var category = Str(el, "category");
                    var color    = Str(el, "color");
                    // Signal — SIG-PRELOAD-CATALOG (v0.23): parse the optional spawnEntityMetadata field
                    // from JSON. When absent for a Boss or Unique rule, default to the Match substring —
                    // the preloaded entity's live Metadata will contain the same substring once spawned,
                    // so reusing Match gives Task 5's scan a working default without hand-authoring the
                    // JSON for every boss. Shrine / Chest / Ritual categories stay null (tile-scoped).
                    var spawnMeta = Str(el, "spawnEntityMetadata");
                    string? resolvedSpawn = spawnMeta.Length > 0
                        ? spawnMeta
                        : (category is "Boss" or "Unique" ? match : null);
                    if (match.Length > 0)
                        rules.Add(new CatalogRule(match, label, tier, category, color, resolvedSpawn));
                }

            // Sort strongest tier first so first substring-match wins with best tier.
            rules.Sort((a, b) => TierRank(b.Tier).CompareTo(TierRank(a.Tier)));

            return new PreloadCatalog(rules.ToArray(), gateRoots.ToArray());
        }
        catch
        {
            return Empty();
        }
    }

    private static PreloadCatalog Empty() => new(Array.Empty<CatalogRule>(), Array.Empty<string>());

    private static Stream? OpenResource(Assembly asm, string contains)
    {
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains(contains));
        return name == null ? null : asm.GetManifestResourceStream(name);
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
