using System.Diagnostics;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Persistent accumulator of distinct "notable" entities/landmarks the overlay has encountered
/// (the catalog-candidate worklist). Mirrors <see cref="ModCatalog"/>: mutations under <c>_gate</c>,
/// a debounced flush to <c>config/seen_pois.json</c>. Read-only w.r.t. the game.
/// </summary>
public sealed class SeenPoiLog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, SeenPoi> _seen = new(StringComparer.Ordinal); // under _gate
    private readonly Stopwatch _sinceDirty = Stopwatch.StartNew();
    private bool _dirty;
    private const long FlushAfterMs = 4000;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SeenPoiLog(string filePath) { _filePath = filePath; Load(); }

    /// <summary>Snapshot of all seen candidates (locked; safe from the API thread).</summary>
    public IReadOnlyList<SeenPoi> All { get { lock (_gate) return _seen.Values.ToArray(); } }

    /// <summary>Record this tick's candidates. Resolves a friendly name only on first sighting;
    /// repeat sightings just bump the count. Call from the world thread.</summary>
    public void Observe(IReadOnlyList<Poe2Live.EntityDot> entities, IReadOnlyList<Poe2Live.Landmark> landmarks, string areaCode)
    {
        lock (_gate)
        {
            foreach (var e in entities)
            {
                if (!PoiCandidate.IsCandidate(in e)) continue;
                // Copy metadata to a value local — a foreach var is capture-safe, but resolve the name
                // lazily (thunk runs only on first sighting). Iterating by value is trivially cheap here.
                var meta = e.Metadata;
                Upsert(PoiCandidate.EntitySignature(in e), meta, null, e.Category.ToString(),
                       e.Poi, e.Rarity.ToString(), () => EntityNameResolver.Shared.ResolveOrShorten(meta), areaCode);
            }
            foreach (var lm in landmarks)
                Upsert(PoiCandidate.LandmarkSignature(lm), null, lm.Path, "Tile",
                       false, "", () => lm.CuratedName ?? lm.Name, areaCode);
        }
        MaybeFlush();
    }

    // Call under _gate. friendlyName is a thunk so we only resolve a name on first insert.
    private void Upsert(string sig, string? metadata, string? landmarkPath, string category,
                        bool poi, string rarity, Func<string> friendlyName, string areaCode)
    {
        if (_seen.TryGetValue(sig, out var cur))
        {
            _seen[sig] = cur with { Count = cur.Count + 1, LastSeenUtc = DateTime.UtcNow };
        }
        else
        {
            _seen[sig] = new SeenPoi(sig, metadata, landmarkPath, category, poi, rarity,
                friendlyName(), ZoneGuide.Shared.FriendlyName(areaCode), 1, DateTime.UtcNow);
        }
        if (!_dirty) { _dirty = true; _sinceDirty.Restart(); }
    }

    private void MaybeFlush()
    {
        lock (_gate)
        {
            if (!_dirty || _sinceDirty.ElapsedMilliseconds < FlushAfterMs) return;
            _dirty = false; Save();
        }
    }

    public void Flush() { lock (_gate) { if (_dirty) { _dirty = false; Save(); } } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<SeenPoi>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var p in list)
                if (!string.IsNullOrEmpty(p.Signature)) _seen[p.Signature] = p;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Seen-POI log load failed: {ex.Message}"); }
    }

    private void Save() // under _gate
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_seen.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Seen-POI log save failed: {ex.Message}"); }
    }
}
