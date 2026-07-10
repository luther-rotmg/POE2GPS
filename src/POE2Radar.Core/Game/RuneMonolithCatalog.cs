using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>
/// The offline runeshape-monolith recipe catalog + the in-game offer rule, decoded from upstream reference's
/// RunecraftHelper (<c>expedition2_recipes.json</c>, built from the game's Expedition2Recipes /
/// Expedition2Runes / Expedition2RunesWeights .dat tables). PURE LOGIC — no memory reads: given a
/// monolith's live state (anchor rune index + hole position, hole count N, area level, and whether it's
/// an anchor-less "unique" monolith), it returns the reward recipes that monolith will offer.
///
/// <para>Pairs with <see cref="Poe2Live.ReadMonolith"/> (which reads the live state off the device →
/// RuneStation chain) so the overlay can show + price a monolith's rewards BEFORE the panel is opened.</para>
///
/// <para>Offer rule (anchored monolith): a recipe is offered iff
/// <c>runeIdx[anchorPos] == anchorIdx</c> AND <c>size &lt;= N</c> AND <c>minLevel &lt;= areaLevel &lt;=
/// maxLevel</c> AND (<c>size == N</c> OR Expedition2RunesWeights permits the partial
/// <c>(anchorIdx, anchorPos+1, size)</c> at this area level). Anchor-less "unique" monolith: every recipe
/// with <c>size &lt;= N</c> within the level band. No category/theme filter.</para>
/// </summary>
public sealed class RuneMonolithCatalog
{
    private readonly List<Recipe> _recipes;
    private readonly Dictionary<int, string> _runeNames;
    private readonly Dictionary<long, int> _partialMinLevel; // (rune,pos1Based,size) → min area level offered

    private RuneMonolithCatalog(List<Recipe> recipes, Dictionary<int, string> runeNames, Dictionary<long, int> partial)
    {
        _recipes = recipes;
        _runeNames = runeNames;
        _partialMinLevel = partial;
    }

    public bool IsLoaded => _recipes.Count > 0;

    /// <summary>The display name of a rune by its Expedition2Runes row index (e.g. 12 → "Cyclonic").</summary>
    public string RuneName(int idx) => _runeNames.TryGetValue(idx, out var n) ? n : $"#{idx}";

    /// <summary>One offered reward: its display name (for pricing via PriceBook.TryByName), reward stack
    /// count, recipe size, and the rune pattern (for tooltips). <paramref name="Name"/> is empty for the
    /// rare reward-less "description" recipes (e.g. dungeon keys) — those carry <paramref name="Description"/>.</summary>
    public readonly record struct Offer(string Name, int Count, int Size, string Runes, string Description);

    /// <summary>The recipes a monolith offers. <paramref name="anchorIdx"/> &lt; 0 (and
    /// <paramref name="isUnique"/> true) selects the anchor-less "all size&lt;=N" branch.
    /// <paramref name="areaLevel"/> &lt;= 0 disables level gating (offers every tier).</summary>
    public List<Offer> Offers(int anchorIdx, int anchorPos, int holeCount, bool isUnique, int areaLevel)
    {
        var result = new List<Offer>();
        if (holeCount <= 0) return result;

        foreach (var rec in _recipes)
        {
            if (rec.size > holeCount) continue;
            if (areaLevel > 0 && rec.maxLevel > 0 && (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;

            if (isUnique || anchorIdx < 0)
            {
                // Anchor-less monolith: the game's offer builder bypasses the anchor + partial gates.
                result.Add(ToOffer(rec));
                continue;
            }

            if (rec.runeIdx is null || rec.runeIdx.Count <= anchorPos) continue;
            if (rec.runeIdx[anchorPos] != anchorIdx) continue;
            // size==N always offered; size<N only when Expedition2RunesWeights enables that partial.
            if (rec.size != holeCount && !IsPartialAllowed(anchorIdx, anchorPos, rec.size, areaLevel)) continue;

            result.Add(ToOffer(rec));
        }
        return result;
    }

    private static Offer ToOffer(Recipe rec) => new(
        rec.reward?.name ?? string.Empty,
        Math.Max(1, rec.rewardCount),
        rec.size,
        rec.runes is null ? string.Empty : string.Join(" · ", rec.runes),
        rec.description ?? string.Empty);

    private bool IsPartialAllowed(int idx, int pos, int size, int areaLevel)
    {
        if (!_partialMinLevel.TryGetValue(PartialKey(idx, pos + 1, size), out var minL)) return false;
        return areaLevel <= 0 || areaLevel >= minL;
    }

    private static long PartialKey(int rune, int pos1Based, int size)
        => ((long)rune << 16) | ((long)pos1Based << 8) | (uint)size;

    // ── loading (embedded, lazy singleton) ──────────────────────────────────────────────────────────
    private static RuneMonolithCatalog? _instance;
    public static RuneMonolithCatalog Instance => _instance ??= LoadEmbedded();

    private static RuneMonolithCatalog LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("expedition2_recipes", StringComparison.Ordinal));
            if (name is null) return Empty();
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return Empty();
            var file = JsonSerializer.Deserialize<CatalogFile>(s, JsonOpts);
            if (file?.recipes is null) return Empty();

            var runeNames = new Dictionary<int, string>();
            if (file.runes is not null)
                foreach (var kv in file.runes)
                    if (int.TryParse(kv.Key, out var k)) runeNames[k] = kv.Value;

            var partial = new Dictionary<long, int>();
            if (file.runeWeights is not null)
                foreach (var w in file.runeWeights)
                {
                    var key = PartialKey(w.rune, w.pos, w.size);
                    if (!partial.TryGetValue(key, out var cur) || w.minLevel < cur) partial[key] = w.minLevel;
                }

            return new RuneMonolithCatalog(file.recipes, runeNames, partial);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RuneMonolithCatalog load failed: {ex.Message}");
            return Empty();
        }
    }

    private static RuneMonolithCatalog Empty()
        => new(new List<Recipe>(), new Dictionary<int, string>(), new Dictionary<long, int>());

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── embedded model (matches expedition2_recipes.json) ────────────────────────────────────────────
    private sealed class CatalogFile
    {
        public Dictionary<string, string>? runes { get; set; }
        public List<Recipe>? recipes { get; set; }
        public List<RuneWeight>? runeWeights { get; set; }
    }

    private sealed class RuneWeight
    {
        public int rune { get; set; }
        public int pos { get; set; }   // 1-based anchor hole position
        public int size { get; set; }
        public int minLevel { get; set; }
    }

    private sealed class Recipe
    {
        public int size { get; set; }
        public List<int>? runeIdx { get; set; }
        public List<string>? runes { get; set; }
        public Reward? reward { get; set; }
        public int rewardCount { get; set; }
        public string? description { get; set; }
        public int minLevel { get; set; }
        public int maxLevel { get; set; }
    }

    private sealed class Reward
    {
        public string name { get; set; } = string.Empty;
    }
}
