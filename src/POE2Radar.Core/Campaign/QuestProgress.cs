using POE2Radar.Core.Game;

namespace POE2Radar.Core.Campaign;

/// <summary>Abstraction over "which campaign step is the player currently working toward". The
/// isolation seam between the always-available zone-order inference and the optional, gated
/// quest-completion memory read. Pure — no memory access in the interface contract.</summary>
public interface IQuestProgress
{
    /// <summary>The step the player should be heading toward, given their current zone.</summary>
    CampaignStep CurrentStep(string currentZoneCode);
}

/// <summary>
/// v1 progress: infer the campaign step from the current zone + a forward-only latch. Entering a
/// later critical-path zone advances the latch; backtracking or wandering off-path keeps pointing at
/// the furthest reached step. Zero memory reads. Stateful (the latch) — one instance per session,
/// touched only by the world thread.
/// </summary>
public sealed class ZoneOrderProgress : IQuestProgress
{
    private readonly CampaignRoute _route;
    private int _furthest;   // ordinal of the furthest critical-path step reached

    public ZoneOrderProgress(CampaignRoute route) => _route = route;

    public CampaignStep CurrentStep(string currentZoneCode)
    {
        var idx = _route.IndexOf(currentZoneCode);
        if (idx > _furthest) _furthest = idx;   // advance forward only
        var steps = _route.Steps;
        if (steps.Count == 0) return default;
        return steps[Math.Min(_furthest, steps.Count - 1)];
    }
}
