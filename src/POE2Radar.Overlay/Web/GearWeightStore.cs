using System.Text.Json;
using POE2Radar.Core.Gear;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Owns the user's gear-scoring config (<c>config/stat_weights.json</c>): a weight per GGG stat id, the
/// raw total that maps to 100, and the 0–100 god-roll threshold. Ships with NO weights — the user assigns
/// them from the dashboard against the real stat ids their own items show (so we never guess stat-id
/// strings, and the weights are genuinely theirs). Read-only w.r.t. the game.
/// </summary>
public sealed class GearWeightStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, double> _byStatId = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private double _target = 100;
    private double _threshold = 85;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GearWeightStore(string filePath) { _filePath = filePath; Load(); }

    /// <summary>Immutable snapshot for the scorer (safe off-thread).</summary>
    public StatWeights Snapshot()
    {
        lock (_gate)
            return new StatWeights(new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase), _target, _threshold);
    }

    /// <summary>Set (or clear, when weight == 0) the weight for a stat id; saves.</summary>
    public void SetWeight(string statId, double weight)
    {
        if (string.IsNullOrWhiteSpace(statId)) return;
        lock (_gate)
        {
            var k = statId.Trim();
            if (weight == 0) _byStatId.Remove(k); else _byStatId[k] = weight;
            Save();
        }
    }

    public void SetTarget(double target) { lock (_gate) { _target = target > 0 ? target : 1; Save(); } }
    public void SetThreshold(double threshold) { lock (_gate) { _threshold = Math.Clamp(threshold, 0, 100); Save(); } }

    /// <summary>The raw config for the dashboard editor (weights + target + threshold).</summary>
    public object View()
    {
        lock (_gate)
            return new { byStatId = new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase), target = _target, godRollThreshold = _threshold };
    }

    private sealed class Model
    {
        public Dictionary<string, double> ByStatId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double Target { get; set; } = 100;
        public double GodRollThreshold { get; set; } = 85;
    }

    private void Load()
    {
        if (JsonStore.TryLoad<Model>(_filePath, Json, "Gear weights", out var m) && m != null)
        {
            _byStatId = new Dictionary<string, double>(m.ByStatId, StringComparer.OrdinalIgnoreCase);
            _target = m.Target > 0 ? m.Target : 1;
            _threshold = Math.Clamp(m.GodRollThreshold, 0, 100);
        }
    }

    private void Save() // under _gate
    {
        try { JsonStore.AtomicWrite(_filePath, new Model { ByStatId = _byStatId, Target = _target, GodRollThreshold = _threshold }, Json); }
        catch (Exception ex) { Console.Error.WriteLine($"Gear weights save failed: {ex.Message}"); }
    }
}
