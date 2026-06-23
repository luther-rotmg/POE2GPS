// src/POE2Radar.Core/Game/DynastyMaps.cs
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Game;

/// <summary>Curated table of endgame maps whose Anomaly bosses drop Lineage/Dynasty Support Gems, keyed by
/// the in-game MapCode (e.g. "MapVaalVault" → "Sealed Vault" — codes differ wildly from display names).
/// Loaded once from the embedded <c>dynasty_maps.json</c>. Read-only.</summary>
public sealed class DynastyMaps
{
    public sealed record DynastyInfo(string Name, string Boss, IReadOnlyList<string> Gems);

    private readonly Dictionary<string, DynastyInfo> _byCode;
    private DynastyMaps(Dictionary<string, DynastyInfo> byCode) => _byCode = byCode;

    public static DynastyMaps Shared { get; } = Load();
    public int Count => _byCode.Count;
    public bool TryGet(string mapCode, out DynastyInfo info) => _byCode.TryGetValue(mapCode, out info!);
    public IReadOnlyDictionary<string, DynastyInfo> All => _byCode;

    private sealed class Model
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("boss")] public string Boss { get; set; } = "";
        [JsonPropertyName("gems")] public List<string> Gems { get; set; } = new();
    }

    private static DynastyMaps Load()
    {
        var byCode = new Dictionary<string, DynastyInfo>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("dynasty_maps", StringComparison.Ordinal));
            if (name != null)
                using (var s = asm.GetManifestResourceStream(name))
                {
                    var parsed = s != null
                        ? JsonSerializer.Deserialize<Dictionary<string, Model>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        : null;
                    if (parsed != null)
                        foreach (var (code, m) in parsed)
                            byCode[code] = new DynastyInfo(m.Name, m.Boss, m.Gems);
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"DynastyMaps load failed: {ex.Message}"); }
        return new DynastyMaps(byCode);
    }
}
