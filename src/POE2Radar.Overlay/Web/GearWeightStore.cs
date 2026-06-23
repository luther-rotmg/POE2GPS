using System.IO;
using System.Text.Json;
using POE2Radar.Core.Game;
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
    private Dictionary<string, double> _normById = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private double _target = 100;
    private double _threshold = 85;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GearWeightStore(string filePath)
    {
        _filePath = filePath;
        var existed = File.Exists(_filePath);
        Load();
        if (!existed) LoadStarter();   // fresh install → ship meta starter weights
    }

    /// <summary>Immutable snapshot for the scorer (safe off-thread).</summary>
    public StatWeights Snapshot()
    {
        lock (_gate)
            return new StatWeights(
                new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase), _target, _threshold,
                new Dictionary<string, double>(_normById, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>Set (or clear, when norm &lt;= 0) the per-stat normalization for a stat id; saves.</summary>
    public void SetNorm(string statId, double norm)
    {
        if (string.IsNullOrWhiteSpace(statId)) return;
        lock (_gate)
        {
            var k = statId.Trim();
            if (norm <= 0) _normById.Remove(k); else _normById[k] = norm;
            Save();
        }
    }

    /// <summary>Replace the current weights + norms with the embedded meta starter set; saves.</summary>
    public void LoadStarter()
    {
        lock (_gate)
        {
            _byStatId = new Dictionary<string, double>(StarterWeights.ByStatId, StringComparer.OrdinalIgnoreCase);
            _normById = new Dictionary<string, double>(StarterWeights.NormById, StringComparer.OrdinalIgnoreCase);
            _target = StarterWeights.Target > 0 ? StarterWeights.Target : 100;
            _threshold = Math.Clamp(StarterWeights.GodRollThreshold, 0, 100);
            Save();
        }
    }

    /// <summary>The raw config for the dashboard editor (weights + norms + target + threshold).</summary>
    public object View()
    {
        lock (_gate)
            return new
            {
                byStatId = new Dictionary<string, double>(_byStatId, StringComparer.OrdinalIgnoreCase),
                normById = new Dictionary<string, double>(_normById, StringComparer.OrdinalIgnoreCase),
                target = _target, godRollThreshold = _threshold,
            };
    }

    private sealed class Model
    {
        public Dictionary<string, double> ByStatId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> NormById { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double Target { get; set; } = 100;
        public double GodRollThreshold { get; set; } = 85;
    }

    private void Load()
    {
        if (JsonStore.TryLoad<Model>(_filePath, Json, "Gear weights", out var m) && m != null)
        {
            _byStatId = new Dictionary<string, double>(m.ByStatId, StringComparer.OrdinalIgnoreCase);
            _normById = new Dictionary<string, double>(m.NormById, StringComparer.OrdinalIgnoreCase);
            _target = m.Target > 0 ? m.Target : 1;
            _threshold = Math.Clamp(m.GodRollThreshold, 0, 100);
        }
    }

    private void Save() // under _gate
    {
        try { JsonStore.AtomicWrite(_filePath, new Model { ByStatId = _byStatId, NormById = _normById, Target = _target, GodRollThreshold = _threshold }, Json); }
        catch (Exception ex) { Console.Error.WriteLine($"Gear weights save failed: {ex.Message}"); }
    }
}
