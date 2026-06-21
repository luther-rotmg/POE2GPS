namespace POE2Radar.Core.Campaign;

/// <summary>Outcome of one director evaluation: whether the nav selection should change, and to
/// which single active objective id (null = clear).</summary>
public readonly record struct DirectorDecision(bool ChangeSelection, string? DesiredActiveId);

/// <summary>
/// Per-zone objective director (pure). Each evaluation ranks → picks the single top objective as the
/// active route, but only when it "owns" the selection: the selection is empty, or exactly the id the
/// director last set. Any other selection is a manual override and the director stands down until the
/// selection returns to its control or <see cref="ResetZone"/> is called (on zone change).
/// </summary>
public sealed class ObjectiveDirector
{
    private string? _lastActiveId;

    /// <summary>The most recently ranked objectives (active first), for the dashboard panel.</summary>
    public IReadOnlyList<RankedObjective> Queue { get; private set; } = System.Array.Empty<RankedObjective>();

    /// <summary>Forget the active objective so the director re-acquires on the next evaluation
    /// (call on zone change, alongside the selection clear).</summary>
    public void ResetZone() => _lastActiveId = null;

    public DirectorDecision Decide(IReadOnlyList<RankedObjective> ranked, IReadOnlyList<string> currentSelectedIds)
    {
        Queue = ranked;
        var desired = ranked.Count > 0 ? ranked[0].Id : null;

        var owns = currentSelectedIds.Count == 0
            || (currentSelectedIds.Count == 1 && currentSelectedIds[0] == _lastActiveId);
        if (!owns) return new DirectorDecision(false, null);

        // Already exactly on the desired selection? nothing to do.
        var alreadyThere = desired == null
            ? currentSelectedIds.Count == 0
            : currentSelectedIds.Count == 1 && currentSelectedIds[0] == desired;
        if (alreadyThere) return new DirectorDecision(false, desired);

        _lastActiveId = desired;
        return new DirectorDecision(true, desired);
    }
}
