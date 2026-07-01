namespace POE2Radar.Core.Game;

/// <summary>
/// Zone-frequency filter that suppresses always-loaded "noise" paths so only genuinely
/// per-zone content surfaces as alerts. Paths seen in ≥ <c>commonThreshold</c> fraction of
/// zones (after <c>warmupZones</c> zones have been observed) are considered common noise and
/// excluded from <see cref="ZoneResult.Alerts"/>. During warmup nothing is suppressed.
/// </summary>
public sealed class PreloadTracker
{
    private readonly int _warmup;
    private readonly double _threshold;
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

    public int ZonesObserved { get; private set; }

    public PreloadTracker(int warmupZones, double commonThreshold)
    {
        _warmup = warmupZones;
        _threshold = commonThreshold;
    }

    public readonly record struct ZoneResult(IReadOnlyList<string> Alerts);

    /// <summary>Record a zone's catalog-matched paths; returns which are NOT "common noise".</summary>
    public ZoneResult ObserveZone(IEnumerable<string> matchedPaths)
    {
        var paths = matchedPaths.Distinct(StringComparer.Ordinal).ToList();
        ZonesObserved++;
        foreach (var p in paths)
            _hits[p] = _hits.GetValueOrDefault(p) + 1;

        var alerts = new List<string>();
        foreach (var p in paths)
        {
            var common = ZonesObserved >= _warmup && (double)_hits[p] / ZonesObserved >= _threshold;
            if (!common) alerts.Add(p);
        }
        return new ZoneResult(alerts);
    }

    /// <summary>Returns per-path (hits, frequency) for diagnostic display.</summary>
    public IReadOnlyDictionary<string, (int hits, double freq)> Snapshot()
        => _hits.ToDictionary(
            k => k.Key,
            k => (k.Value, ZonesObserved == 0 ? 0.0 : (double)k.Value / ZonesObserved),
            StringComparer.Ordinal);

    /// <summary>Restore persisted state (e.g. from JSON on startup).</summary>
    public void Load(int zonesObserved, IReadOnlyDictionary<string, int> hits)
    {
        ZonesObserved = zonesObserved;
        foreach (var kv in hits) _hits[kv.Key] = kv.Value;
    }

    /// <summary>Export state for persistence (e.g. to JSON on shutdown).</summary>
    public (int zones, IReadOnlyDictionary<string, int> hits) Export()
        => (ZonesObserved, _hits);
}
