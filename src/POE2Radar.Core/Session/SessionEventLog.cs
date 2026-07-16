using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace POE2Radar.Core.Session;

/// <summary>
/// Per-character rolling log of session events (deaths, level-ups, boss kills, drops)
/// backing the v0.37 Character Codex. Mirrors DropTimeline's storage seams:
/// load-on-construct + append-on-record + flush-on-dispose; LinkedList ring cap with
/// oldest-first eviction; corrupt-file → fresh in-memory state.
///
/// On-disk format is JSONL: one CodexEvent serialized per line, no envelope object,
/// no pretty-printing. Whole-file rewrite on Flush (needed anyway for the ring
/// eviction to actually shrink the file).
///
/// Adds a character-name STABILITY GATE: the log will not open/create a per-character
/// file until the observed PlayerName has been non-empty and unchanged for
/// NameStabilityTicks (30) consecutive ObservePlayerName() calls. Defeats the login
/// and character-swap flicker documented in Poe2Live.PlayerName's cache profile
/// (empty-frame under zone load, address change on swap).
///
/// Not thread-safe against concurrent Dispose; all other members are lock-safe under
/// the internal _gate.
/// </summary>
public sealed class SessionEventLog : IDisposable
{
    public const int MaxEntriesPerCharacter = 5000;
    /// <summary>~1 second at 30 Hz world-tick cadence. Defeats login flicker where PlayerName briefly reads as a stale/empty value during the character-swap window.</summary>
    public const int NameStabilityTicks     = 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        // JSONL: never pretty-print; each event must serialize to a single line.
        WriteIndented        = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configDir;
    private readonly object _gate = new();

    // Currently-open character (null = no file open, all Record calls dropped).
    private string?                 _openName;
    private readonly LinkedList<CodexEvent> _entries = new();
    private bool                    _dirty;
    private int                     _generation;

    // Stability-gate state.
    private string? _candidate;
    private int     _stableTicks;

    public SessionEventLog(string configDir) { _configDir = configDir; }

    public int     Generation     { get { lock (_gate) return _generation; } }
    public string? OpenCharacter  { get { lock (_gate) return _openName; } }

    /// <summary>Feed the current-tick PlayerName reading. Null or empty resets the stability counter but does NOT close an already-open file.</summary>
    public void ObservePlayerName(string? name)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(name))
            {
                _candidate   = null;
                _stableTicks = 0;
                return;
            }
            if (name == _candidate) _stableTicks++;
            else { _candidate = name; _stableTicks = 1; }

            if (_stableTicks >= NameStabilityTicks && name != _openName)
            {
                FlushLocked();
                _openName = name;
                LoadLocked(name);
                _generation++;
            }
        }
    }

    /// <summary>Append a CodexEvent under the currently-open character. Returns false if no character is open yet.</summary>
    public bool Record(CodexEvent ev)
    {
        if (ev is null) return false;
        lock (_gate)
        {
            if (_openName is null) return false;
            _entries.AddLast(ev);
            while (_entries.Count > MaxEntriesPerCharacter) _entries.RemoveFirst();
            _generation++;
            _dirty = true;
            return true;
        }
    }

    public IReadOnlyList<CodexEvent> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>Stateless read of the on-disk jsonl file for an arbitrary character name.
    /// Does NOT touch the currently-open character; safe to call from /api/codex handlers
    /// serving cross-character requests. Returns empty list if the file is missing or corrupt.</summary>
    public IReadOnlyList<CodexEvent> SnapshotForCharacter(string name)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<CodexEvent>();
        var path = PathFor(name);
        if (!File.Exists(path)) return Array.Empty<CodexEvent>();
        var result = new List<CodexEvent>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<CodexEvent>(line, Json);
                    if (ev != null) result.Add(ev);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* corrupt file → empty result */ }
        return result;
    }

    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        if (!_dirty || _openName is null) return;
        var snap = _entries.ToArray();
        _dirty = false;
        var path = PathFor(_openName);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var ev in snap)
            {
                sb.Append(JsonSerializer.Serialize(ev, Json));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* nice-to-have; silent — matches DropTimeline.Flush */ }
    }

    private void LoadLocked(string name)
    {
        _entries.Clear();
        _dirty = false;
        var path = PathFor(name);
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Per-line try/catch — schema-drift defense; one bad row doesn't nuke the load.
                try
                {
                    var ev = JsonSerializer.Deserialize<CodexEvent>(line, Json);
                    if (ev != null)
                    {
                        _entries.AddLast(ev);
                        while (_entries.Count > MaxEntriesPerCharacter) _entries.RemoveFirst();
                    }
                }
                catch { /* skip malformed line, keep valid neighbors */ }
            }
        }
        catch { _entries.Clear(); /* corrupt file → fresh start; never throws */ }
    }

    private string PathFor(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "_unknown";
        return Path.Combine(_configDir, "codex", safe + ".jsonl");
    }

    public void Dispose() => Flush();
}
