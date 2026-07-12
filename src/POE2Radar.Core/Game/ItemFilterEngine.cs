using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>v0.31 Prospector: one requirement inside an <see cref="FilterRule"/>. Every requirement
/// in a rule must be satisfied for the rule to match (AND-group). The <see cref="Op"/> is one of
/// <c>&gt;=</c>, <c>&lt;=</c>, <c>==</c>, <c>between</c>. When <see cref="Scope"/> is set,
/// candidate affixes are restricted to the named slots ("implicit", "prefix", "suffix"). When
/// <see cref="MaxTier"/> is set, candidate affixes must have <see cref="ModRanges.TierFor"/> ≤ this.</summary>
public sealed record FilterRequirement(
    [property: JsonPropertyName("statId")]  string StatId,
    [property: JsonPropertyName("op")]      string Op,
    [property: JsonPropertyName("value")]   double Value,
    [property: JsonPropertyName("valueMax")] double? ValueMax = null,
    [property: JsonPropertyName("scope")]   IReadOnlyList<string>? Scope = null,
    [property: JsonPropertyName("maxTier")] int? MaxTier = null);

/// <summary>v0.31 Prospector: one user-authored filter rule. Requirements are AND-linked. On item-
/// matches across multiple filters, the winning filter (highest <see cref="Priority"/>) supplies
/// the border <see cref="Color"/>. Ties broken by list order.</summary>
public sealed record FilterRule(
    [property: JsonPropertyName("id")]           string Id,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("enabled")]      bool Enabled,
    [property: JsonPropertyName("color")]        string Color,
    [property: JsonPropertyName("priority")]     int Priority,
    [property: JsonPropertyName("requirements")] IReadOnlyList<FilterRequirement> Requirements);

/// <summary>
/// v0.31 Prospector: user's item filter ruleset — persistent JSON-backed list of highlight
/// rules that match affix combinations on items. Load-on-construct + save-on-mutate, mirroring
/// the DisplayRules pattern (lock + immutable snapshot + generation counter). Missing file /
/// malformed file → falls back to the shipped starter presets (all disabled). First run
/// materializes those seeds on disk so users can edit through the dashboard.
/// The <c>Match</c> method (added in T6) is the render-hot path; keep it allocation-free per call.
/// </summary>
public sealed class ItemFilterEngine
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<FilterRule> _rules = new();
    private int _generation;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ItemFilterEngine(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public int Generation { get { lock (_gate) return _generation; } }
    public IReadOnlyList<FilterRule> All { get { lock (_gate) return _rules.ToList(); } }

    public void Replace(IEnumerable<FilterRule> rules)
    {
        lock (_gate) { _rules = rules.ToList(); _generation++; Save(); }
    }
    public void Add(FilterRule rule)
    {
        lock (_gate) { _rules.Add(rule); _generation++; Save(); }
    }
    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules.RemoveAt(index); _generation++; Save();
        }
    }
    public void Update(int index, FilterRule rule)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules[index] = rule; _generation++; Save();
        }
    }
    public void Move(int fromIndex, int toIndex)
    {
        lock (_gate)
        {
            if (fromIndex < 0 || fromIndex >= _rules.Count) return;
            toIndex = Math.Clamp(toIndex, 0, _rules.Count - 1);
            if (fromIndex == toIndex) return;
            var r = _rules[fromIndex];
            _rules.RemoveAt(fromIndex);
            _rules.Insert(toIndex, r);
            _generation++; Save();
        }
    }

    /// <summary>Append any shipped starter presets whose id is NOT already in the current list.
    /// Never overwrites, never removes — additive-only, safe to call at any time.</summary>
    public void RestoreStarterPresets()
    {
        var seed = LoadEmbeddedPresets();
        lock (_gate)
        {
            var existingIds = new HashSet<string>(_rules.Select(r => r.Id), StringComparer.Ordinal);
            foreach (var p in seed)
                if (!existingIds.Contains(p.Id)) _rules.Add(p);
            _generation++; Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var seed = LoadEmbeddedPresets();
                _rules = seed.ToList();
                Save();
                return;
            }
            var doc = JsonSerializer.Deserialize<FilterFile>(File.ReadAllText(_filePath), Json);
            if (doc?.Filters is not null) _rules = doc.Filters.ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"Item filters load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(new FilterFile(_rules.ToList()), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Item filters save failed: {ex.Message}"); }
    }

    /// <summary>Read the shipped <c>default_item_filters.json</c> embedded resource. Returns an
    /// empty list on missing / malformed resource (fail-safe — never throws).</summary>
    public static IReadOnlyList<FilterRule> LoadEmbeddedPresets()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("default_item_filters", StringComparison.Ordinal));
            if (name is null) return Array.Empty<FilterRule>();
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return Array.Empty<FilterRule>();
            var doc = JsonSerializer.Deserialize<FilterFile>(s, Json);
            return doc?.Filters ?? Array.Empty<FilterRule>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Item filter presets load failed: {ex.Message}");
            return Array.Empty<FilterRule>();
        }
    }

    private sealed record FilterFile([property: JsonPropertyName("filters")] IReadOnlyList<FilterRule> Filters);
}
