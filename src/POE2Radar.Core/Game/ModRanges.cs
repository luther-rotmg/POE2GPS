using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>Embedded per-mod ROLL RANGES + derived TIER (from RePoE via the Research --gen-ranges probe).
/// Lets a consumer turn a rolled value into "% of max" and "T# of N". Read-only; loaded once.</summary>
public sealed class ModRanges
{
    public readonly record struct StatRange(string Id, double Min, double Max);
    public sealed record ModRangeInfo(IReadOnlyList<StatRange> Stats, int Tier, int TierCount);

    private readonly Dictionary<string, ModRangeInfo> _byMod;
    private ModRanges(Dictionary<string, ModRangeInfo> byMod) => _byMod = byMod;

    public static ModRanges Shared { get; } = Load();
    public int Count => _byMod.Count;
    public bool TryGet(string modId, out ModRangeInfo info) => _byMod.TryGetValue(modId, out info!);

    private sealed class StatModel { [JsonPropertyName("id")] public string Id { get; set; } = ""; [JsonPropertyName("min")] public double Min { get; set; } [JsonPropertyName("max")] public double Max { get; set; } }
    private sealed class ModModel { [JsonPropertyName("stats")] public List<StatModel> Stats { get; set; } = new(); [JsonPropertyName("tier")] public int Tier { get; set; } [JsonPropertyName("tierCount")] public int TierCount { get; set; } }

    private static ModRanges Load()
    {
        var byMod = new Dictionary<string, ModRangeInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("poe2_mod_ranges", StringComparison.Ordinal));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                {
                    var parsed = s != null ? JsonSerializer.Deserialize<Dictionary<string, ModModel>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
                    if (parsed != null)
                        foreach (var (modId, m) in parsed)
                            byMod[modId] = new ModRangeInfo(m.Stats.Select(x => new StatRange(x.Id, x.Min, x.Max)).ToList(), m.Tier, m.TierCount);
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"ModRanges load failed: {ex.Message}"); }
        return new ModRanges(byMod);
    }
}
