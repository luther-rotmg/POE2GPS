using System;
using System.Collections.Generic;
using POE2Radar.Core.Session;

namespace POE2Radar.Core.Codex;

/// <summary>
/// v0.37 C4: subscribes to DropTimeline.Recorded and forwards notable drops
/// (unique-rarity + first-seen-item-per-character) to the codex sink as
/// NotableDropEvents. Per-character dedup ensures a duplicate roll of the
/// same unique in the same session doesn't fill the codex with noise.
/// Non-unique drops are silently dropped.
/// </summary>
public sealed class CodexDropForwarder
{
    private readonly Action<CodexEvent> _sink;
    // Per-character set of already-forwarded item names/arts. Session-scoped —
    // we WANT the same unique to re-log for a different character (their first).
    private readonly Dictionary<string, HashSet<string>> _seenByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CodexDropForwarder(Action<CodexEvent> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>Wire this to <c>DropTimeline.Recorded</c>. Filters + forwards inside.</summary>
    public void OnDropRecorded(DropEntry drop)
    {
        if (drop is null) return;
        // Only unique-rarity items are "notable" per v0.37 spec.
        if (!string.Equals(drop.Rarity, "Unique", StringComparison.OrdinalIgnoreCase)) return;
        var character = drop.CharacterName ?? string.Empty;
        var key = drop.Name ?? string.Empty;
        if (key.Length == 0) return;
        lock (_gate)
        {
            if (!_seenByCharacter.TryGetValue(character, out var seen))
            {
                seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _seenByCharacter[character] = seen;
            }
            if (!seen.Add(key)) return; // already forwarded this unique for this character this session
        }
        _sink(new NotableDropEvent(
            drop.TimestampSec,
            0u, // AreaHash unknown at DropTimeline layer (only AreaCode is captured); 0 = "unknown"
            drop.AreaCode ?? string.Empty,
            drop.Name ?? string.Empty,
            drop.Rarity ?? "Unique",
            null)); // Art not currently in DropEntry; A2 UI falls back to name
    }
}
