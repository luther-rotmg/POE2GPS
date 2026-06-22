using System.Diagnostics;
using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// A persistent, ever-growing catalog of every monster affix-mod id the overlay has ever read
/// (e.g. <c>MonsterPhysicalDamageAura1</c>, <c>MonsterEnergyShieldAura1</c>). Monster mods aren't in
/// any shipped list and new ones arrive with content patches, so we accumulate them as they're seen
/// live and persist to <c>config/known_mods.json</c> — the vocabulary the dashboard's rule editor
/// browses when authoring a <see cref="DisplayRule.Mods"/> matcher. Append-only: ids are never
/// removed, so a rule term stays discoverable even after you leave the zone that surfaced it.
///
/// <para>Fed from the world tick (<see cref="Observe"/>); writes are debounced off the hot path.
/// Reads (<see cref="All"/>) come from the API thread, hence the lock.</para>
/// </summary>
public sealed class ModCatalog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly SortedSet<string> _mods = new(StringComparer.Ordinal); // ordinal-sorted for stable output
    private readonly Stopwatch _sinceDirty = Stopwatch.StartNew();
    private bool _dirty;
    private const long FlushAfterMs = 4000; // debounce: coalesce a burst of new mods into one write

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ModCatalog(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>Snapshot of every known mod id, ordinal-sorted (safe to serialize off-thread).</summary>
    public IReadOnlyList<string> All { get { lock (_gate) return _mods.ToList(); } }

    public int Count { get { lock (_gate) return _mods.Count; } }

    /// <summary>Record any newly-seen mod ids from one entity. Cheap when nothing is new (the common
    /// case once a zone is mapped); marks the catalog dirty + schedules a debounced flush otherwise.</summary>
    public void Observe(IReadOnlyList<string>? mods)
    {
        if (mods is not { Count: > 0 }) return;
        lock (_gate)
        {
            var added = false;
            for (var i = 0; i < mods.Count; i++)
                if (_mods.Add(mods[i])) added = true;
            if (!added) return;
            if (!_dirty) { _dirty = true; _sinceDirty.Restart(); }
        }
    }

    /// <summary>Record mods from a whole entity list (called once per world tick).</summary>
    public void Observe(IEnumerable<Poe2Live.EntityDot> entities)
    {
        foreach (var e in entities)
            if (e.Mods is { Count: > 0 }) Observe(e.Mods);
        MaybeFlush();
    }

    /// <summary>Persist if dirty and the debounce window has elapsed. Safe to call every tick.</summary>
    public void MaybeFlush()
    {
        lock (_gate)
        {
            if (!_dirty || _sinceDirty.ElapsedMilliseconds < FlushAfterMs) return;
            _dirty = false;
            Save();
        }
    }

    /// <summary>Force a write if anything is pending (call on shutdown so the last burst isn't lost).</summary>
    public void Flush()
    {
        lock (_gate) { if (_dirty) { _dirty = false; Save(); } }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var m in list) if (!string.IsNullOrWhiteSpace(m)) _mods.Add(m);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Mod catalog load failed: {ex.Message}"); }
    }

    // Call under _gate.
    private void Save()
    {
        try
        {
            JsonStore.AtomicWrite(_filePath, _mods.ToList(), Json);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Mod catalog save failed: {ex.Message}"); }
    }
}
