using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Game;

/// <summary>
/// Turns the in-memory item-mod data we read (internal mod id + rolled value(s)) into the game's
/// English stat lines, e.g. <c>IncreasedLife5 [67] → "+67 to maximum Life"</c>.
///
/// <para>Two embedded reference tables, generated from the community RePoE PoE2 export
/// (repoe-fork.github.io/poe2) and trimmed to what items need:
/// <list type="bullet">
/// <item><c>poe2_mod_stats.json</c> — mod id → ordered stat ids (the mod's <c>stats[]</c>).</item>
/// <item><c>poe2_stat_descriptions.json</c> — RePoE stat_descriptions: each <c>{ids[], English[]}</c>
/// rule maps one-or-more stat ids + their values to a formatted line (with conditions, value
/// index_handlers like <c>negate</c>, and <c>[ref|display]</c> tag markup).</item>
/// </list>
/// We read the rolled VALUE from process memory, so the mod's value RANGES are not needed — only the
/// stat-id mapping and the description templates. Patch-volatile data: regenerate both files from the
/// matching RePoE export after a content patch (see resources/poe2-data).</para>
/// </summary>
public sealed class ItemModTranslator
{
    private readonly Dictionary<string, string[]> _modStats;     // mod id → ordered stat ids
    private readonly Dictionary<string, List<StatDesc>> _byStat; // stat id → description rules touching it

    private ItemModTranslator(Dictionary<string, string[]> modStats, Dictionary<string, List<StatDesc>> byStat)
    {
        _modStats = modStats;
        _byStat = byStat;
    }

    /// <summary>The shared translator, loaded once from the embedded tables.</summary>
    public static ItemModTranslator Shared { get; } = LoadEmbedded();

    /// <summary>True when both tables loaded (non-empty).</summary>
    public bool IsLoaded => _modStats.Count > 0 && _byStat.Count > 0;
    public int ModCount => _modStats.Count;

    /// <summary>The ordered stat ids a mod grants, or null if the mod id is unknown.</summary>
    public string[]? StatIdsFor(string modId) => _modStats.GetValueOrDefault(modId);

    private Dictionary<string, string[]>? _renderedIndex;
    private readonly object _renderedIndexGate = new();

    /// <summary>The stat ids for a fully value-templated stat line (numbers shown as the format token, e.g.
    /// <c>"+# to maximum Life"</c> / <c>"#% increased Movement Speed"</c>), or null if unrecognized. Used by
    /// the offline starter-weights generator to bridge Tincture's meta stat lines to our stat ids.</summary>
    public string[]? StatIdsForRenderedLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        EnsureRenderedIndex();
        return _renderedIndex!.GetValueOrDefault(NormalizeLine(line));
    }

    private void EnsureRenderedIndex()
    {
        if (_renderedIndex != null) return;
        lock (_renderedIndexGate)
        {
            if (_renderedIndex != null) return;
            var idx = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var list in _byStat.Values)
            foreach (var entry in list)
            foreach (var rule in entry.English)
            {
                var s = rule.String ?? "";
                var fmt = rule.Format ?? Array.Empty<string>();
                for (var i = 0; i < fmt.Length; i++)
                {
                    var token = "{" + i + "}";
                    s = fmt[i].Contains("ignore", StringComparison.Ordinal) ? s.Replace(token, "") : s.Replace(token, fmt[i]);
                }
                var key = NormalizeLine(CleanTags(s));
                if (key.Length > 0) idx.TryAdd(key, entry.Ids);
            }
            _renderedIndex = idx;
        }
    }

    private static string NormalizeLine(string s) =>
        Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();

    /// <summary>
    /// Render one item mod to its English stat line(s). <paramref name="values"/> are the rolled
    /// values read from the mod's ModArrayStruct, in stat order. Returns one entry per displayed line
    /// (multi-stat mods like "Adds # to # Damage" collapse to a single line). Unknown mods / stats
    /// fall back to a readable <c>id = value</c> so nothing is silently dropped.
    /// </summary>
    public List<string> RenderMod(string modId, IReadOnlyList<int> values)
    {
        var statIds = _modStats.GetValueOrDefault(modId);
        if (statIds == null || statIds.Length == 0)
            return new List<string> { $"{modId} ({string.Join(",", values)})" };

        // Pair each stat id with its value (positional). Missing values default to 0.
        var pending = new List<(string id, int val)>();
        for (var i = 0; i < statIds.Length; i++)
            pending.Add((statIds[i], i < values.Count ? values[i] : 0));

        var lines = new List<string>();
        var present = pending.Select(p => p.id).ToHashSet();
        int ValOf(string id) => pending.FirstOrDefault(p => p.id == id).id == id ? pending.First(p => p.id == id).val : 0;

        // Candidate description blocks that reference any present stat. A block may list companion
        // stat ids the mod doesn't carry (GGG writes some lines for a stat pair); the game renders
        // those treating the absent stat as 0. So: prefer FULLY-present blocks (real multi-stat lines
        // like "Adds # to # Damage"), then most-covered, then longest — and fill absent ids with 0.
        var candidates = present
            .SelectMany(id => _byStat.GetValueOrDefault(id) ?? (IEnumerable<StatDesc>)Array.Empty<StatDesc>())
            .Distinct()
            .Select(e => (e, coverage: e.Ids.Count(present.Contains)))
            .Where(x => x.coverage > 0)
            .OrderByDescending(x => x.coverage == x.e.Ids.Length) // full matches first
            .ThenByDescending(x => x.coverage)
            .ThenByDescending(x => x.e.Ids.Length)
            .Select(x => x.e)
            .ToList();

        var consumed = new HashSet<string>();
        foreach (var entry in candidates)
        {
            // Skip if this block adds nothing new (all its present ids are already explained).
            if (entry.Ids.Where(present.Contains).All(consumed.Contains)) continue;
            var vals = entry.Ids.Select(id => present.Contains(id) ? ValOf(id) : 0).ToArray();
            var line = RenderEntry(entry, vals);
            if (line == null) continue;          // no condition rule matched these values
            foreach (var id in entry.Ids.Where(present.Contains)) consumed.Add(id);
            if (line.Length > 0) lines.Add(line); // empty = fully "ignore"d line
        }

        // Stats with no description rule that matched → raw fallback (so nothing is silently dropped).
        foreach (var (id, val) in pending)
            if (!consumed.Contains(id))
                lines.Add($"{id} ({val})");

        return lines;
    }

    // Render a single description entry given the values for its ids (entry-id order).
    private static string? RenderEntry(StatDesc entry, int[] vals)
    {
        foreach (var rule in entry.English)
        {
            if (!ConditionsMatch(rule.Condition, vals)) continue;

            var s = rule.String ?? "";
            for (var i = 0; i < vals.Length; i++)
            {
                var handlers = rule.IndexHandlers != null && i < rule.IndexHandlers.Length ? rule.IndexHandlers[i] : null;
                double hv = vals[i];
                if (handlers != null)
                    foreach (var h in handlers) hv = ApplyHandler(hv, h);

                var fmt = rule.Format != null && i < rule.Format.Length ? rule.Format[i] : "#";
                var token = "{" + i + "}";
                if (fmt.Contains("ignore", StringComparison.Ordinal)) { s = s.Replace(token, ""); continue; }
                var num = FormatNumber(hv);
                if (fmt.StartsWith('+') && hv >= 0) num = "+" + num;
                s = s.Replace(token, num);
            }
            return CleanTags(s).Trim();
        }
        return null;
    }

    private static bool ConditionsMatch(Cond[]? conds, int[] vals)
    {
        if (conds == null) return true;
        for (var i = 0; i < vals.Length; i++)
        {
            if (i >= conds.Length) continue;
            var c = conds[i];
            if (c == null) continue;
            if (c.Min.HasValue && vals[i] < c.Min.Value) return false;
            if (c.Max.HasValue && vals[i] > c.Max.Value) return false;
        }
        return true;
    }

    private static double ApplyHandler(double v, string h) => h switch
    {
        "negate" => -v,
        "divide_by_one_hundred_and_negate" => -v / 100.0,
        "divide_by_one_hundred" => v / 100.0,
        "divide_by_one_hundred_2dp" or "divide_by_one_hundred_2dp_if_required" => v / 100.0,
        "divide_by_two" => v / 2.0,
        "divide_by_three" => v / 3.0,
        "divide_by_four" => v / 4.0,
        "divide_by_five" => v / 5.0,
        "divide_by_ten" => v / 10.0,
        "divide_by_twenty" => v / 20.0,
        "divide_by_fifty" => v / 50.0,
        "divide_by_one_thousand" => v / 1000.0,
        "milliseconds_to_seconds" => v / 1000.0,
        "milliseconds_to_seconds_1dp" or "milliseconds_to_seconds_2dp" => v / 1000.0,
        "deciseconds_to_seconds" => v / 10.0,
        "per_minute_to_per_second" or "per_minute_to_per_second_1dp" or "per_minute_to_per_second_2dp" => v / 60.0,
        "times_twenty" => v * 20.0,
        "times_one_point_five" => v * 1.5,
        "multiplicative_damage_modifier" or "multiplicative_permyriad_damage_modifier" => v,
        _ => v, // unknown / no-op (canonical_line, mod_value_to_item_class, etc.)
    };

    private static string FormatNumber(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString();
        return Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // [Reference|Display] -> Display ; [Text] -> Text
    private static readonly Regex TagRx = new(@"\[([^\]]*)\]", RegexOptions.Compiled);
    private static string CleanTags(string s) =>
        TagRx.Replace(s, m => { var inner = m.Groups[1].Value; var bar = inner.LastIndexOf('|'); return bar >= 0 ? inner[(bar + 1)..] : inner; });

    private static ItemModTranslator LoadEmbedded()
    {
        var modStats = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var byStat = new Dictionary<string, List<StatDesc>>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            using (var s = OpenRes(asm, "poe2_mod_stats"))
            {
                if (s != null)
                {
                    using var doc = JsonDocument.Parse(s);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        modStats[p.Name] = p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                }
            }

            using (var s = OpenRes(asm, "poe2_stat_descriptions"))
            {
                if (s != null)
                {
                    var entries = JsonSerializer.Deserialize<StatDesc[]>(s, JsonOpts) ?? Array.Empty<StatDesc>();
                    foreach (var e in entries)
                        foreach (var id in e.Ids)
                            (byStat.TryGetValue(id, out var list) ? list : byStat[id] = new List<StatDesc>()).Add(e);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ItemModTranslator load failed: {ex.Message}");
        }
        return new ItemModTranslator(modStats, byStat);
    }

    private static Stream? OpenRes(Assembly asm, string contains)
    {
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains(contains, StringComparison.Ordinal));
        return name == null ? null : asm.GetManifestResourceStream(name);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Embedded stat_descriptions model (RePoE format) ──
    private sealed class StatDesc
    {
        [JsonPropertyName("ids")] public string[] Ids { get; set; } = Array.Empty<string>();
        [JsonPropertyName("English")] public EnglishRule[] English { get; set; } = Array.Empty<EnglishRule>();
    }

    private sealed class EnglishRule
    {
        [JsonPropertyName("condition")] public Cond[]? Condition { get; set; }
        [JsonPropertyName("format")] public string[]? Format { get; set; }
        [JsonPropertyName("index_handlers")] public string[][]? IndexHandlers { get; set; }
        [JsonPropertyName("string")] public string? String { get; set; }
    }

    private sealed class Cond
    {
        [JsonPropertyName("min")] public int? Min { get; set; }
        [JsonPropertyName("max")] public int? Max { get; set; }
    }
}
