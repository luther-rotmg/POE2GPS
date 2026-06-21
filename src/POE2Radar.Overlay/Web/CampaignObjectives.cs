using System.Reflection;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// User-managed campaign-objective catalog: a priority-ranked list of matchers over the live
/// entity/landmark data, persisted to <c>config/campaign_objectives.json</c> and seeded from the
/// embedded <c>default_campaign_objectives.json</c> on first run. Same lock+snapshot discipline as
/// <see cref="WatchedEntities"/>: mutations under <c>_gate</c>, a volatile immutable
/// <see cref="ObjectiveCatalog"/> snapshot for lock-free <see cref="Rank"/> on the world thread.
/// </summary>
public sealed class CampaignObjectives
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, CampaignObjective> _entries = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private volatile ObjectiveCatalog _snapshot = new(Array.Empty<CampaignObjective>());                     // immutable
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CampaignObjectives(string filePath)
    {
        _filePath = filePath;
        Load();
        if (_entries.Count == 0) { LoadDefaults(); Save(); }
        Rebuild();
    }

    /// <summary>Rank the objectives present in the current zone (lock-free: reads the snapshot).</summary>
    public IReadOnlyList<RankedObjective> Rank(
        IReadOnlyList<Poe2Live.EntityDot> entities, IReadOnlyList<Poe2Live.Landmark> landmarks, Vector2 player)
        => _snapshot.Rank(entities, landmarks, player);

    public IReadOnlyList<CampaignObjective> All { get { lock (_gate) return _entries.Values.ToArray(); } }

    public void Add(CampaignObjective o)
    {
        if (o is null || string.IsNullOrWhiteSpace(o.Id)) return;
        lock (_gate) { _entries[o.Id] = o; Rebuild(); Save(); }
    }

    public void Remove(string id)
    {
        lock (_gate) { if (_entries.Remove(id)) { Rebuild(); Save(); } }
    }

    /// <summary>Rebuild the immutable catalog snapshot. Call under <see cref="_gate"/>.</summary>
    private void Rebuild() => _snapshot = new ObjectiveCatalog(_entries.Values.ToArray());

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<CampaignObjective>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var o in list)
                if (!string.IsNullOrWhiteSpace(o.Id)) _entries[o.Id] = o;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives load failed: {ex.Message}"); }
    }

    private void LoadDefaults()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("default_campaign_objectives"));
            if (resName == null) return;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return;
            using var sr = new StreamReader(stream);
            var list = JsonSerializer.Deserialize<List<CampaignObjective>>(sr.ReadToEnd(), Json);
            if (list == null) return;
            foreach (var o in list)
                if (!string.IsNullOrWhiteSpace(o.Id)) _entries[o.Id] = o;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives defaults failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Campaign objectives save failed: {ex.Message}"); }
    }
}
