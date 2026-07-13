using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Session;

/// <summary>v0.33 Drop Timeline: one recorded drop entry. Timestamp is Unix seconds (UTC).
/// EntityId is the game's entity id (uint from Poe2Live.EntityDot.Id) for per-session dedup;
/// zone is the game AreaCode at drop time; character is the observing player's name.</summary>
public sealed record DropEntry(
    [property: JsonPropertyName("ts")]        long TimestampSec,
    [property: JsonPropertyName("rarity")]    string Rarity,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("zone")]      string AreaCode,
    [property: JsonPropertyName("character")] string CharacterName,
    [property: JsonPropertyName("entityId")]  uint EntityId);

/// <summary>v0.33 Drop Timeline: persistent per-session record of ground-item drops the
/// player observed. Load-on-construct + append-on-record + debounced-save. Ring buffer
/// capped at <see cref="MaxEntries"/>; oldest entries drop first on overflow. In-memory
/// dedup by EntityId prevents double-recording the same drop within a session.
/// Mirrors the BossWipeLog persistence pattern from v0.30 Instinct. Local file only —
/// no telemetry, no pricing, no egress.</summary>
public sealed class DropTimeline
{
    public const int MaxEntries = 1000;

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly LinkedList<DropEntry> _entries = new();   // oldest at head, newest at tail
    private readonly HashSet<uint> _seenEntityIds = new();      // session-scoped dedup
    private int _generation;
    private bool _dirty;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Generation { get { lock (_gate) return _generation; } }

    public DropTimeline(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>Record a new drop. No-op if name is null/empty, or if entityId was already
    /// recorded this session (in-memory dedup — the persistent file is NOT consulted, so
    /// the same drop across restarts would be recorded again; that matches user expectation
    /// since each session is a distinct play window).</summary>
    public void Record(long timestampSec, string rarity, string? name, string areaCode, string characterName, uint entityId)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_gate)
        {
            if (!_seenEntityIds.Add(entityId)) return;
            _entries.AddLast(new DropEntry(timestampSec, rarity ?? "Normal", name, areaCode ?? "", characterName ?? "", entityId));
            while (_entries.Count > MaxEntries) _entries.RemoveFirst();
            _generation++;
            _dirty = true;
        }
    }

    /// <summary>Snapshot of the current entries in insertion order (oldest → newest).
    /// Returned list is a fresh copy — safe to iterate without lock.</summary>
    public IReadOnlyList<DropEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>Flush any pending writes. Idempotent when clean. Never throws (I/O failures
    /// swallowed — this is a nice-to-have, not a data-integrity system).</summary>
    public void Flush()
    {
        DropEntry[] snap;
        lock (_gate)
        {
            if (!_dirty) return;
            snap = _entries.ToArray();
            _dirty = false;
        }
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(new { drops = snap }, Json));
        }
        catch { /* nice-to-have; silent */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("drops", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var el in arr.EnumerateArray())
            {
                var entry = JsonSerializer.Deserialize<DropEntry>(el.GetRawText(), Json);
                if (entry != null) _entries.AddLast(entry);
                while (_entries.Count > MaxEntries) _entries.RemoveFirst();
            }
        }
        catch { _entries.Clear(); /* corrupt file → fresh start; never throws */ }
    }
}