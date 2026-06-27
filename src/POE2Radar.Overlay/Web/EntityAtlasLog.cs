using System.Diagnostics;
using System.Text.Json;
using POE2Radar.Core.Campaign;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Persistent accumulator of the full distinct-entity census (every non-junk, non-player metadata
/// path the overlay has encountered) — the Atlas naming/coverage worklist. Mirrors the hardened
/// <see cref="SeenPoiLog"/>: mutations under <c>_gate</c>, periodic flush only on a NEW signature,
/// count drift persisted at shutdown. Read-only w.r.t. the game.
/// </summary>
public sealed class EntityAtlasLog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, AtlasEntry> _seen = new(StringComparer.Ordinal); // under _gate
    private readonly Stopwatch _sinceDirty = Stopwatch.StartNew();
    private bool _dirty;        // a NEW census signature arrived → arm the periodic debounced flush
    private bool _countsDirty;  // only repeat-sighting drift → persisted at shutdown, never on the loop
    private const long FlushAfterMs = 4000;
    private const int MaxEntries = 10000;
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public EntityAtlasLog(string filePath) { _filePath = filePath; Load(); }

    /// <summary>Snapshot of the whole census (locked; safe from the API thread).</summary>
    public IReadOnlyList<AtlasEntry> All { get { lock (_gate) return _seen.Values.ToArray(); } }

    /// <summary>Record this tick's entities into the census. Skips the player + JunkFilter noise via
    /// <see cref="AtlasCensus.IsCensusEntity"/>; dedups by metadata signature. Call from the world
    /// thread with the PRE-cull entity list (so user-hidden entities still get catalogued).</summary>
    public void Observe(IReadOnlyList<Poe2Live.EntityDot> entities, string areaCode)
    {
        lock (_gate)
        {
            foreach (var e in entities)
            {
                if (!AtlasCensus.IsCensusEntity(in e)) continue;
                var sig = AtlasCensus.Signature(in e);
                if (_seen.TryGetValue(sig, out var cur))
                {
                    // Repeat sighting: bump in memory, but do NOT arm the periodic flush (mirrors the
                    // hardened SeenPoiLog — avoids an every-4s whole-file rewrite for the whole session).
                    _seen[sig] = cur with { Count = cur.Count + 1, LastSeenUtc = DateTime.UtcNow };
                    _countsDirty = true;
                }
                else
                {
                    if (_seen.Count >= MaxEntries) continue; // cap reached — no new keys
                    var now = DateTime.UtcNow;
                    _seen[sig] = new AtlasEntry(sig, e.Category.ToString(), e.Rarity.ToString(), e.Poi,
                        ZoneGuide.Shared.FriendlyName(areaCode), 1, now, now);
                    if (!_dirty) { _dirty = true; _sinceDirty.Restart(); }
                }
            }
        }
        MaybeFlush();
    }

    private void MaybeFlush()
    {
        lock (_gate)
        {
            if (!_dirty || _sinceDirty.ElapsedMilliseconds < FlushAfterMs) return;
            _dirty = false; _countsDirty = false; Save();
        }
    }

    public void Flush() { lock (_gate) { if (_dirty || _countsDirty) { _dirty = false; _countsDirty = false; Save(); } } }

    private void Load()
    {
        if (JsonStore.TryLoad<List<AtlasEntry>>(_filePath, Json, "Entity atlas", out var list) && list != null)
            foreach (var a in list)
                if (!string.IsNullOrEmpty(a.Metadata)) _seen[a.Metadata] = a;
    }

    private void Save() // under _gate
    {
        try
        {
            JsonStore.AtomicWrite(_filePath, _seen.Values.ToList(), Json);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity atlas save failed: {ex.Message}"); }
    }
}
