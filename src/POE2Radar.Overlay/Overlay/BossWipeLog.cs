using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// v0.30 Instinct: persistent per-character boss wipe counter. Persisted as
/// <c>boss_wipe_log.json</c> next to the other config files. Structure is
/// <c>{ characters: { charName: { bosses: { bossKey: count } } } }</c> — keyed by the
/// <see cref="POE2Radar.Core.Game.BossEncounterCatalog.EncounterEntry.Key"/> so entries survive
/// zone-code renames and merge across multiple boss arenas that share a catalog entry. Uses a
/// per-character <see cref="WipeMemory"/> internally so the unit-tested counter logic is not
/// duplicated. Load-on-construct + save-on-mutate; fail-safe against a corrupt / missing file
/// (starts empty, never throws).
/// </summary>
public sealed class BossWipeLog
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, WipeMemory> _byChar = new(StringComparer.Ordinal);
    // Persist model — kept internal + only serialized JSON has the "bosses" wrapper for
    // forward-compat with per-character metadata (last-death timestamps, etc. — future v0.31+).
    private sealed record CharEntry([property: JsonPropertyName("bosses")] Dictionary<string, int> Bosses);
    private sealed record RootModel([property: JsonPropertyName("characters")] Dictionary<string, CharEntry> Characters);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BossWipeLog(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>Wipe count for one character-boss pair. Zero on null/empty inputs or unknown pair.
    /// Thread-safe.</summary>
    public int Count(string? charName, string? bossKey)
    {
        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(bossKey)) return 0;
        lock (_gate) return GetOrCreate(charName!).Count(bossKey);
    }

    /// <summary>Record a death: increments <c>{charName, bossKey}</c> by 1, persists, and returns
    /// the new count. No-op on null/empty inputs (returns 0). Thread-safe.</summary>
    public int RecordDeath(string? charName, string? bossKey)
    {
        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(bossKey)) return 0;
        lock (_gate) return GetOrCreate(charName!).RecordDeath(bossKey);
    }

    /// <summary>All boss-key → count entries for one character (empty snapshot when unknown).
    /// Copies the internal map so callers can enumerate off-thread. Thread-safe.</summary>
    public IReadOnlyDictionary<string, int> ForCharacter(string? charName)
    {
        if (string.IsNullOrEmpty(charName)) return new Dictionary<string, int>();
        lock (_gate)
        {
            if (!_byChar.TryGetValue(charName!, out var mem)) return new Dictionary<string, int>();
            return mem.Snapshot();
        }
    }

    /// <summary>All character names currently on record. Ordered by character name for stable UI.</summary>
    public IReadOnlyList<string> Characters()
    {
        lock (_gate) return _byChar.Keys.OrderBy(k => k).ToList();
    }

    /// <summary>Drop one boss entry for a character (dashboard "forget this boss" button).</summary>
    public bool ClearBoss(string? charName, string? bossKey)
    {
        if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(bossKey)) return false;
        lock (_gate)
        {
            if (!_byChar.TryGetValue(charName!, out var mem)) return false;
            return mem.ClearZone(bossKey);
        }
    }

    /// <summary>Drop every wipe entry for one character (dashboard "reset character" button).</summary>
    public void ClearCharacter(string? charName)
    {
        if (string.IsNullOrEmpty(charName)) return;
        lock (_gate)
        {
            if (_byChar.Remove(charName!)) Save();
        }
    }

    /// <summary>Total wipes across all bosses for one character (dashboard summary).</summary>
    public int TotalFor(string? charName)
    {
        if (string.IsNullOrEmpty(charName)) return 0;
        lock (_gate)
        {
            if (!_byChar.TryGetValue(charName!, out var mem)) return 0;
            return mem.Snapshot().Values.Sum();
        }
    }

    private WipeMemory GetOrCreate(string charName)
    {
        if (_byChar.TryGetValue(charName, out var existing)) return existing;
        var fresh = new WipeMemory(new Dictionary<string, int>(), Save);
        _byChar[charName] = fresh;
        return fresh;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var model = JsonSerializer.Deserialize<RootModel>(File.ReadAllText(_filePath), Json);
            if (model?.Characters is null) return;
            foreach (var (name, ch) in model.Characters)
            {
                if (string.IsNullOrEmpty(name) || ch?.Bosses is null) continue;
                var dict = new Dictionary<string, int>(ch.Bosses);
                _byChar[name] = new WipeMemory(dict, Save);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"BossWipeLog load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var characters = new Dictionary<string, CharEntry>(StringComparer.Ordinal);
            foreach (var (name, mem) in _byChar)
            {
                var snap = mem.Snapshot();
                if (snap.Count == 0) continue;
                characters[name] = new CharEntry(new Dictionary<string, int>(snap));
            }
            var model = new RootModel(characters);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(model, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"BossWipeLog save failed: {ex.Message}"); }
    }
}
