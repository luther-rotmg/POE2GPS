// Ported from ExileCampaigns2 by syrairc under TODO(syrairc-license) — upstream commit TODO(syrairc-hash)
using System;
using System.Collections.Generic;

namespace POE2Radar.Core.Campaign.Guide;

// the only seam between the pure advance logic and live game memory. WorldStateAdapter (later task) will
// implement this against POE2Radar's live-memory snapshot; tests fake it. methods take matchers/patterns
// and return instantaneous truth or a per-current-step progress count (the runtime maintains the counters
// and resets them on step change).
public interface IWorldState
{
    bool QuestFlagSatisfied(Pattern flag);
    bool InAreaSatisfied(Pattern area);
    bool WaypointPulsed();
    bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities, IReadOnlyList<Pattern>? tiles, float distance);
    bool LootSatisfied(IReadOnlyList<ItemMatcher>? items);
    int KillProgress(IReadOnlyList<EntityMatcher>? entities);
    int InteractProgress(IReadOnlyList<EntityMatcher>? entities);
    int TalkProgress(IReadOnlyList<EntityMatcher>? entities);
    int SatisfiedFlagCount(IReadOnlyList<Pattern> flags);
}

// evaluates whether the current step is done. the single advance gate.
public static class AdvanceEngine
{
    public const float DefaultProximity = 40f;

    public static bool IsStepSatisfied(RouteStep step, IWorldState world)
    {
        if (step?.Objectives is null || step.Objectives.Count == 0) return false;
        if (step.CompleteWhen == CompleteWhen.Any)
        {
            foreach (var o in step.Objectives)
                if (ObjectiveSatisfied(o, world)) return true;
            return false;
        }
        foreach (var o in step.Objectives)
            if (!ObjectiveSatisfied(o, world)) return false;
        return true;
    }

    public static bool ObjectiveSatisfied(Objective o, IWorldState world) => o.Type switch
    {
        ObjectiveType.Kill             => CountDone(o, world, world.KillProgress),
        ObjectiveType.Interact         => CountDone(o, world, world.InteractProgress),
        ObjectiveType.Talk             => CountDone(o, world, world.TalkProgress),
        ObjectiveType.Loot             => world.LootSatisfied(o.Items),
        ObjectiveType.Proximity        => world.ProximitySatisfied(o.Entities, null, o.Distance > 0f ? o.Distance : DefaultProximity),
        ObjectiveType.QuestFlag        => o.Flag != null && world.QuestFlagSatisfied(o.Flag),
        ObjectiveType.EnterArea        => o.AreaTarget != null && world.InAreaSatisfied(o.AreaTarget),
        ObjectiveType.ActivateWaypoint => world.WaypointPulsed(),
        ObjectiveType.Manual           => false,
        _                              => false,
    };

    // Kill/Interact/Talk: progress flags win when present (e.g. the 3 obelisks), else the live counter.
    private static bool CountDone(Objective o, IWorldState world, Func<IReadOnlyList<EntityMatcher>?, int> liveCount)
    {
        int needed = o.Count > 0 ? o.Count : 1;
        int done = o.ProgressFlags is { Count: > 0 } ? world.SatisfiedFlagCount(o.ProgressFlags) : liveCount(o.Entities);
        return done >= needed;
    }
}
