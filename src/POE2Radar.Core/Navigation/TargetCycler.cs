namespace POE2Radar.Core.Navigation;

/// <summary>
/// Pure cycle/select logic over an already-ranked list of target ids. Tracks the active target BY ID
/// (not index), so it stays correct as the list rebuilds and targets despawn every tick — Next/Prev
/// re-find the current id in the fresh list before moving. No game/UI dependency; fully unit-testable.
/// </summary>
public static class TargetCycler
{
    /// <summary>The id after <paramref name="current"/>, wrapping to the first. If current is null or no
    /// longer present, returns the first id. Null when the list is empty.</summary>
    public static string? Next(IReadOnlyList<string> ranked, string? current)
    {
        if (ranked.Count == 0) return null;
        var i = current is null ? -1 : IndexOf(ranked, current);
        return i < 0 ? ranked[0] : ranked[(i + 1) % ranked.Count];
    }

    /// <summary>The id before <paramref name="current"/>, wrapping to the last. If current is null or no
    /// longer present, returns the last id. Null when the list is empty.</summary>
    public static string? Prev(IReadOnlyList<string> ranked, string? current)
    {
        if (ranked.Count == 0) return null;
        var i = current is null ? -1 : IndexOf(ranked, current);
        return i < 0 ? ranked[^1] : ranked[(i - 1 + ranked.Count) % ranked.Count];
    }

    /// <summary>The id at 1-based slot <paramref name="oneBased"/> (1 = first), or null if out of range.</summary>
    public static string? AtIndex(IReadOnlyList<string> ranked, int oneBased)
    {
        var i = oneBased - 1;
        return (uint)i < (uint)ranked.Count ? ranked[i] : null;
    }

    private static int IndexOf(IReadOnlyList<string> ranked, string id)
    {
        for (var i = 0; i < ranked.Count; i++) if (ranked[i] == id) return i;
        return -1;
    }
}
