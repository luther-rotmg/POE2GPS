namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// v0.30 Instinct: per-zone wipe counter — future-you sees what past-you learned. Wraps a
/// dictionary of areaCode → death count from <see cref="POE2Radar.Overlay.Config.RadarSettings.ZoneWipeCounts"/>
/// and calls the provided change callback (usually settings.Save) whenever it mutates. Pure
/// synchronous logic; safe to call from the render thread and unit-testable in isolation
/// (see PanelMemoryTests).
/// </summary>
public sealed class WipeMemory
{
    private readonly Dictionary<string, int> _counts;
    private readonly System.Action _onChanged;

    /// <summary>Construct against a persisted dict (usually <c>settings.ZoneWipeCounts</c>). The
    /// dict is mutated in-place; <paramref name="onChanged"/> fires after every write so the caller
    /// can persist. A null dict is tolerated — an empty one is created internally.</summary>
    public WipeMemory(Dictionary<string, int>? counts, System.Action onChanged)
    {
        _counts = counts ?? new Dictionary<string, int>();
        _onChanged = onChanged ?? (() => { });
    }

    /// <summary>How many times you've died in this areaCode across all past sessions. 0 for
    /// null/empty codes or codes never wiped in. Safe under concurrent reads (dictionary reads
    /// are safe for reference-typed values while no writer runs).</summary>
    public int Count(string? areaCode)
        => !string.IsNullOrEmpty(areaCode) && _counts.TryGetValue(areaCode!, out var n) ? n : 0;

    /// <summary>Increment the wipe count for the given areaCode by 1. No-op on null/empty codes so
    /// a bad read never poisons the dict. Fires <see cref="_onChanged"/> so settings get persisted.</summary>
    public int RecordDeath(string? areaCode)
    {
        if (string.IsNullOrEmpty(areaCode)) return 0;
        var next = Count(areaCode) + 1;
        _counts[areaCode!] = next;
        _onChanged();
        return next;
    }

    /// <summary>Clear the wipe count for one areaCode (dashboard "forget this zone" button).
    /// Returns true if an entry was removed, false if none existed.</summary>
    public bool ClearZone(string? areaCode)
    {
        if (string.IsNullOrEmpty(areaCode)) return false;
        var removed = _counts.Remove(areaCode!);
        if (removed) _onChanged();
        return removed;
    }

    /// <summary>Wipe out the whole memory (dashboard "reset" button). Fires onChanged even when
    /// empty so a downstream config save always runs — the caller may be persisting other fields
    /// too and expecting the flush.</summary>
    public void ClearAll()
    {
        _counts.Clear();
        _onChanged();
    }

    /// <summary>Snapshot of the current state (readable copy, no reference to internal dict).</summary>
    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(_counts);
}
