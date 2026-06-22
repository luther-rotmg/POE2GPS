using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// Owns the user's friendly-name overrides (<c>config/entity_names_user.json</c>, a flat
/// metadata→name map) and keeps them live in <see cref="EntityNameResolver"/>. Naming an entity
/// updates the map, re-installs the overrides (radar/legend reflect it immediately), and saves.
/// Writes are rare (user actions), so the save is immediate (no debounce). Read-only w.r.t. the game.
/// </summary>
public sealed class EntityNameStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EntityNameStore(string filePath) { _filePath = filePath; Load(); Publish(); }

    /// <summary>Snapshot of the user name map (locked; for export).</summary>
    public IReadOnlyDictionary<string, string> All
    { get { lock (_gate) return new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase); } }

    /// <summary>Set/clear one friendly name; installs it live + saves. Blank name reverts to embedded.</summary>
    public void Add(string metadata, string name)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return;
        lock (_gate)
        {
            var key = metadata.Trim();
            if (string.IsNullOrWhiteSpace(name)) _names.Remove(key); else _names[key] = name.Trim();
            Save();
        }
        Publish();
    }

    /// <summary>Merge a batch (community import); installs + saves once. Blank value clears that key.</summary>
    public void Merge(IReadOnlyDictionary<string, string> names)
    {
        if (names is not { Count: > 0 }) return;
        lock (_gate)
        {
            foreach (var (k, v) in names)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var key = k.Trim();
                if (string.IsNullOrWhiteSpace(v)) _names.Remove(key); else _names[key] = v.Trim();
            }
            Save();
        }
        Publish();
    }

    /// <summary>Save anything pending (Dispose parity; immediate-save makes this a safety net).</summary>
    public void Flush() { lock (_gate) Save(); }

    // Install the current map as the resolver's override layer (atomic swap inside the resolver).
    private void Publish()
    {
        Dictionary<string, string> copy;
        lock (_gate) copy = new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase);
        EntityNameResolver.Shared.SetUserOverrides(copy);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath), Json);
            if (map == null) return;
            foreach (var (k, v) in map)
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) _names[k] = v;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity name store load failed: {ex.Message}"); }
    }

    private void Save() // under _gate
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_names, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Entity name store save failed: {ex.Message}"); }
    }
}
