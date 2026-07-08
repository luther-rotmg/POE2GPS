using System;
using System.Collections.Generic;

namespace POE2Radar.Core.Campaign.Guide;

/// <summary>
/// World-thread-owned ordinal cursor over <see cref="RouteModel.Steps"/>. Advances forward one step
/// at a time whenever <see cref="AdvanceEngine.IsStepSatisfied"/> reports the current step done, and
/// forward-snaps past preceding steps on area change (spec §6 graceful-degradation path for the
/// stubbed-signal steps that ship in v0.21). Publishes an immutable
/// <see cref="CampaignStepInstruction"/> snapshot for the SSE payload builder via the same
/// volatile-reference-swap discipline as <c>RadarApp._campaignGps</c> — a single volatile reference
/// field, no lock, torn-read-free because readers only ever load the atomic reference and index an
/// immutable one-slot array.
/// </summary>
/// <remarks>
/// <para>Mutation contract: <see cref="Tick"/> and <see cref="OnAreaChange"/> are world-thread-only
/// (called from <c>RadarApp.CampaignReconcile</c>); the SSE-payload thread reads
/// <see cref="CurrentInstruction"/> and <see cref="CurrentOrdinal"/> — no other writer exists. Zero
/// memory writes to the game process at any point; the cursor only reads
/// <see cref="IWorldState"/> plus its own private ordinal.</para>
/// <para>Not persisted — a radar restart resets the cursor to ordinal 0. Task 5 (EC2-WIRE) drives
/// <see cref="Tick"/> and <see cref="OnAreaChange"/>; task 6 (EC2-UI) reads
/// <see cref="CurrentInstruction"/> off the SSE payload.</para>
/// </remarks>
public sealed class RouteCursor
{
    private readonly RouteModel _route;

    // World-thread writer; SSE-thread reads via CurrentOrdinal getter. Volatile int on all
    // CLR-supported architectures reads atomically, so no torn ordinal.
    private volatile int _ordinal;

    // World-thread only — records the last area handed to OnAreaChange so classification can flag
    // "you left the route" (step.AreaId != currentArea) as a distinct kind of stall.
    private string? _currentArea;

    // Reference-swap publication: writers construct a new one-element array with the fresh snapshot
    // and volatile-store the reference; readers volatile-load the reference and index [0]. Because
    // volatile writes have release semantics, the struct fields inside the array are guaranteed
    // fully written before the reader can see the new reference — no torn snapshot possible.
    // Empty array = "no current instruction" (route empty or completed).
    private volatile CampaignStepInstruction[] _published;

    private static readonly CampaignStepInstruction[] EmptyPublish = Array.Empty<CampaignStepInstruction>();

    public RouteCursor(RouteModel route)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _ordinal = 0;
        _published = _route.Steps.Count > 0
            ? new[] { BuildSnapshot(0) }
            : EmptyPublish;
    }

    /// <summary>Current cursor position (0-based). Reads are atomic across threads.</summary>
    public int CurrentOrdinal => _ordinal;

    /// <summary>Immutable current-step snapshot for SSE publication. <c>null</c> when the route is
    /// empty or the cursor has walked off the end.</summary>
    public CampaignStepInstruction? CurrentInstruction
    {
        get
        {
            var p = _published;
            return p.Length == 0 ? (CampaignStepInstruction?)null : p[0];
        }
    }

    /// <summary>World-thread per-tick advance. Walks forward while
    /// <see cref="AdvanceEngine.IsStepSatisfied"/> reports true for the current step, republishing
    /// the snapshot exactly once if anything moved. Cheap when nothing changes — no allocation, no
    /// lock, so a Tick loop that never satisfies is free.</summary>
    public void Tick(IWorldState world)
    {
        if (world is null) return;
        var steps = _route.Steps;
        var idx = _ordinal;
        if (idx >= steps.Count) return;

        var moved = false;
        // Bounded — capped at steps.Count iterations. Stops at the first stall so ordinal stays
        // monotonic within the run and the caller can observe partial progress next tick.
        while (idx < steps.Count && AdvanceEngine.IsStepSatisfied(steps[idx], world))
        {
            idx++;
            moved = true;
        }
        if (moved)
        {
            _ordinal = idx;
            Republish();
        }
    }

    /// <summary>Area-change forward-snap (spec §6 graceful degradation). Walks from the current
    /// ordinal forward and moves the cursor to the first step whose
    /// <see cref="RouteStep.AreaId"/> matches <paramref name="newAreaId"/>. If nothing ahead lives
    /// in the new area, the cursor stays put but the republished snapshot flips
    /// <see cref="CampaignStepInstruction.Stalled"/> so the UI can badge "you left the route".
    /// Never rewinds.</summary>
    public void OnAreaChange(string newAreaId)
    {
        if (string.IsNullOrEmpty(newAreaId)) return;
        if (string.Equals(newAreaId, _currentArea, StringComparison.OrdinalIgnoreCase)) return;
        _currentArea = newAreaId;

        var steps = _route.Steps;
        var idx = _ordinal;
        if (idx >= steps.Count)
        {
            Republish();
            return;
        }

        var snapAt = -1;
        for (var i = idx; i < steps.Count; i++)
        {
            if (string.Equals(steps[i].AreaId, newAreaId, StringComparison.OrdinalIgnoreCase))
            {
                snapAt = i;
                break;
            }
        }
        if (snapAt >= 0 && snapAt != idx)
        {
            _ordinal = snapAt;
        }
        // Always republish: the current-area context (used by ClassifyStep) may have flipped the
        // Stalled/DegradationReason fields even when the ordinal didn't move.
        Republish();
    }

    private void Republish()
    {
        var idx = _ordinal;
        _published = idx >= _route.Steps.Count
            ? EmptyPublish
            : new[] { BuildSnapshot(idx) };
    }

    private CampaignStepInstruction BuildSnapshot(int ordinal)
    {
        var s = _route.Steps[ordinal];
        var (stalled, available, reason) = Classify(s, _currentArea);
        return new CampaignStepInstruction(
            StepId: s.Id,
            Text: s.Text,
            AreaId: s.AreaId,
            Act: s.Act,
            Ordinal: ordinal,
            TotalSteps: _route.Steps.Count,
            Optional: s.Optional,
            Stalled: stalled,
            Available: available,
            DegradationReason: reason);
    }

    // Two independent stall causes surfaced in v0.21:
    //   1. Area-mismatch: player is somewhere other than the current step's AreaId. The step can
    //      never satisfy from here — the cursor is pinned, waiting for a re-entry.
    //   2. Signal-stub: every path this step could advance through routes at least one stubbed
    //      IWorldState method (QuestFlag/Waypoint/SatisfiedFlagCount/Talk/Interact hard-return
    //      zero/false until PMS-4 in v0.22). Cursor will never satisfy this step automatically.
    // Available flips true whenever there's at least one live path — the UI can still draw
    // partial-progress badging even for a stalled step.
    // Empty-objective steps are treated as ordinary manual/text steps (not stalled) — a route
    // author can drop pure "narrative" lines without them being flagged as v0.21 gaps.
    private static (bool Stalled, bool Available, string? Reason) Classify(RouteStep step, string? currentArea)
    {
        if (!string.IsNullOrEmpty(currentArea) &&
            !string.IsNullOrEmpty(step.AreaId) &&
            !string.Equals(step.AreaId, currentArea, StringComparison.OrdinalIgnoreCase))
        {
            var available = HasAnyLiveObjective(step);
            return (true, available, $"you're in {currentArea}, route expects {step.AreaId}");
        }

        var objs = step.Objectives;
        if (objs is null || objs.Count == 0)
        {
            // No advance signal to inspect. Treat as a manual-advance/narrative step: not a v0.21
            // gap, not "available" for auto-progress either.
            return (false, false, null);
        }

        var live = 0;
        var stub = 0;
        string? firstStubReason = null;
        foreach (var o in objs)
        {
            if (IsObjectiveLiveSignal(o)) live++;
            else
            {
                stub++;
                firstStubReason ??= StubReasonFor(o);
            }
        }

        if (step.CompleteWhen == CompleteWhen.Any)
        {
            // Any single live objective can finish the step.
            var stalledAny = live == 0;
            var availableAny = live > 0;
            return (stalledAny, availableAny, stalledAny ? firstStubReason : null);
        }
        // CompleteWhen.All — one stubbed objective is enough to block the whole step.
        var stalledAll = stub > 0;
        var availableAll = live > 0;
        return (stalledAll, availableAll, stalledAll ? firstStubReason : null);
    }

    private static bool HasAnyLiveObjective(RouteStep step)
    {
        var objs = step.Objectives;
        if (objs is null) return false;
        foreach (var o in objs)
            if (IsObjectiveLiveSignal(o)) return true;
        return false;
    }

    // Live signals (task 3 WorldStateAdapter): InAreaSatisfied, ProximitySatisfied, KillProgress
    // (entity-list snapshot), and LootSatisfied via the player-inventory bag walk. Everything else
    // is a graceful-degradation stub that hard-returns false/0 until PMS-4 lands in v0.22:
    //   QuestFlag / Waypoint / SatisfiedFlagCount / Talk / Interact / Manual / quest-inventory Loot.
    // The ProgressFlags side-channel on Kill/Interact/Talk routes through SatisfiedFlagCount, so a
    // Kill with ProgressFlags populated is stubbed even though a bare Kill is live.
    private static bool IsObjectiveLiveSignal(Objective o) => o.Type switch
    {
        ObjectiveType.Kill             => o.ProgressFlags is not { Count: > 0 },
        ObjectiveType.Interact         => false,
        ObjectiveType.Talk             => false,
        ObjectiveType.Loot             => true,
        ObjectiveType.Proximity        => true,
        ObjectiveType.QuestFlag        => false,
        ObjectiveType.EnterArea        => true,
        ObjectiveType.ActivateWaypoint => false,
        ObjectiveType.Manual           => false,
        _                              => false,
    };

    private static string StubReasonFor(Objective o) => o.Type switch
    {
        ObjectiveType.Interact         => "InteractProgress stubbed until PMS-4 (v0.22)",
        ObjectiveType.Talk             => "TalkProgress stubbed until PMS-4 (v0.22)",
        ObjectiveType.QuestFlag        => "QuestFlagSatisfied stubbed until PMS-4 (v0.22)",
        ObjectiveType.ActivateWaypoint => "WaypointPulsed stubbed until PMS-4 (v0.22)",
        ObjectiveType.Kill             => "ProgressFlags path routes through stubbed SatisfiedFlagCount (v0.22)",
        ObjectiveType.Manual           => "manual-advance only",
        _                              => "no live signal for this objective type in v0.21",
    };
}
