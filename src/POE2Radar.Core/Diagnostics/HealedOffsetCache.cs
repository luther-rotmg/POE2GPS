using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Thread-safe cache of runtime-discovered ("healed") offsets. When a patch shifts a memory offset,
/// the probe families (B1..B8) discover the correct value at runtime and store it here. The overlay's
/// read layer calls <see cref="Resolve"/> instead of using the configured value directly, so a
/// healed offset takes effect immediately without a restart.
/// <para>
/// Persistence (<c>healed-offsets.json</c>) is written atomically on every heal event. The cache
/// is also hydrated from persistence at startup via <see cref="Load"/>, which applies 30-day
/// staleness filtering.
/// </para>
/// </summary>
public static class HealedOffsetCache
{
    private static readonly ConcurrentDictionary<string, HealedEntry> _entries = new(StringComparer.Ordinal);
    private static readonly object _logLock = new();
    private static string? _configDir;

    /// <summary>
    /// All currently active healed entries. Snapshot-safe (materialized on access).
    /// </summary>
    public static IReadOnlyList<HealedEntry> All => _entries.Values.ToList().AsReadOnly();

    /// <summary>
    /// Clear all healed entries and reset internal state. Used for testing.
    /// </summary>
    public static void Clear()
    {
        _entries.Clear();
        _configDir = null;
    }

    /// <summary>
    /// Load the cache from <c>healed-offsets.json</c> in <paramref name="configDir"/>.
    /// This is called at Poe2Live startup. Any entry older than <paramref name="maxAge"/>
    /// (default 30 days) is dropped. If <paramref name="signaturePassesGate"/> is provided,
    /// entries whose configured offset passes the gate (indicating the game patched back to
    /// the configured value) are also dropped — stale heals should not leak through.
    /// </summary>
    /// <param name="configDir">The config directory containing <c>healed-offsets.json</c>.</param>
    /// <param name="maxAge">Maximum entry age before staleness. Default 30 days.</param>
    /// <param name="signaturePassesGate">
    /// Optional delegate: <c>(symbolName, configuredOffset) → true</c> if the configured offset
    /// passes the family-specific plausibility gate and thus the healed entry is stale.
    /// Provided by each probe family (B1..B8) to self-heal when the game patches back.
    /// </param>
    public static void Load(string configDir, TimeSpan? maxAge = null, Func<string, int, bool>? signaturePassesGate = null)
    {
        _configDir = configDir;
        maxAge ??= TimeSpan.FromDays(30);

        var content = HealedOffsetsFile.Load(configDir);
        var now = DateTime.UtcNow;

        foreach (var entry in content.Healed)
        {
            var stale = (now - entry.HealedUtc) > maxAge.Value;
            if (!stale && signaturePassesGate != null)
            {
                stale = signaturePassesGate(entry.Symbol, entry.Configured);
            }

            if (!stale)
            {
                _entries.TryAdd(entry.Symbol, entry);
            }
        }
    }

    /// <summary>
    /// Resolve an offset for the given symbol. If a healed entry exists, returns the healed value;
    /// otherwise returns <paramref name="configuredOffset"/>.
    /// Thread-safe.
    /// </summary>
    public static int Resolve(string symbolName, int configuredOffset)
    {
        if (_entries.TryGetValue(symbolName, out var entry))
            return entry.Healed;
        return configuredOffset;
    }

    /// <summary>
    /// Register a healed offset for a symbol. Performs loud logging to <c>Console.Out</c> and
    /// the rolling log file, then enqueues a persistence save. Idempotent — setting the same
    /// symbol to the same value logs again (helps track how often a heal fires).
    /// Never throws on log/persistence failure.
    /// </summary>
    public static void SetHealed(string symbolName, int healedOffset)
    {
        var now = DateTime.UtcNow;
        // Use the previously-healed or configured value (from existing entry) as 'Configured'.
        // For a fresh heal with no prior entry, Configured is set to 0 (the caller is expected
        // to have the configured value via Resolve at their call site, but SetHealed follows the
        // spec signature which only receives symbol + healed).
        var configured = Resolve(symbolName, 0);
        var entry = new HealedEntry(symbolName, configured, healedOffset, now);

        _entries[symbolName] = entry;

        // Loud log to Console.Out
        try
        {
            Console.Out.WriteLine(
                $"[OFFSET-HEAL] {symbolName}: configured=0x{configured:X} → healed=0x{healedOffset:X} (session UTC {now:O})");
        }
        catch { /* best-effort */ }

        // Append to log file
        try
        {
            AppendToLogFile(symbolName, configured, healedOffset, now);
        }
        catch { /* best-effort — log failure must never throw out of SetHealed */ }

        // Persist to healed-offsets.json
        try
        {
            Persist();
        }
        catch { /* best-effort — persistence failure must never throw out of SetHealed */ }
    }

    /// <summary>
    /// Returns <c>true</c> if a healed entry exists for the given symbol.
    /// </summary>
    public static bool WasHealed(string symbolName) =>
        _entries.ContainsKey(symbolName);

    /// <summary>
    /// Remove entries older than <paramref name="maxAge"/>, optionally invoking a callback
    /// for each removed entry. Thread-safe.
    /// </summary>
    /// <param name="maxAge">Entries older than this are removed.</param>
    /// <param name="onReValidate">Optional callback invoked with <c>(symbolName, configuredOffset)</c>
    /// for each removed entry. Probe families use this to trigger re-discovery.</param>
    public static void InvalidateStale(TimeSpan maxAge, Action<string, int>? onReValidate)
    {
        var now = DateTime.UtcNow;
        var staleSymbols = new List<string>();

        foreach (var kvp in _entries)
        {
            if ((now - kvp.Value.HealedUtc) > maxAge)
            {
                staleSymbols.Add(kvp.Key);
            }
        }

        foreach (var symbol in staleSymbols)
        {
            if (_entries.TryRemove(symbol, out var removed))
            {
                onReValidate?.Invoke(removed.Symbol, removed.Configured);
            }
        }

        // If any entries were removed, persist the trimmed cache
        if (staleSymbols.Count > 0)
        {
            try { Persist(); } catch { /* best-effort */ }
        }
    }

    private static void AppendToLogFile(string symbol, int configured, int healed, DateTime utc)
    {
        var dir = _configDir;
        if (string.IsNullOrEmpty(dir)) return;

        var logPath = Path.Combine(dir, "healed-offsets.log");
        // Ensure directory exists
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var line = $"{utc:O}\t{symbol}\t0x{configured:X}\t0x{healed:X}";
        lock (_logLock)
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
    }

    private static void Persist()
    {
        var dir = _configDir;
        if (string.IsNullOrEmpty(dir)) return;

        var content = new HealedOffsetsFileContent(1, _entries.Values.ToList().AsReadOnly());
        HealedOffsetsFile.Save(dir, content);
    }
}