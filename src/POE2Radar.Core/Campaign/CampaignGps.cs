using POE2Radar.Core.Game;
using V2 = System.Numerics.Vector2;

namespace POE2Radar.Core.Campaign;

/// <summary>The cross-zone navigation instruction for the current tick. <see cref="ExitObjectiveId"/>
/// is a nav-selection id ("t:&lt;landmarkKey&gt;" / "e:&lt;entityId&gt;") the existing routing pipeline
/// resolves, or null when no usable exit is visible in this zone.</summary>
public readonly record struct GpsInstruction(string? ExitObjectiveId, string TargetName, int Act, string Text);

/// <summary>
/// Pure cross-zone campaign GPS. Given the player's current zone, the (zone-order or quest-aware)
/// progress, and the live in-zone landmarks/entities, decide which exit to route toward to advance the
/// campaign — and the human instruction text. No memory access; no state.
/// </summary>
public static class CampaignGps
{
    public static GpsInstruction Decide(string currentZoneCode, IQuestProgress progress, CampaignRoute route,
        IReadOnlyList<Poe2Live.Landmark> landmarks, IReadOnlyList<Poe2Live.EntityDot> entities, V2 player)
    {
        var target = progress.CurrentStep(currentZoneCode);
        var inTarget = string.Equals(currentZoneCode, target.Zone, StringComparison.OrdinalIgnoreCase);

        // Where are we trying to go FROM this zone?
        //  - in the target zone → forward to the next step (its name + the target's exitHint).
        //  - off the target zone → back toward the target zone (by the target's own name).
        string? wantName; string? wantCode; string? exitHint;
        if (inTarget)
        {
            var next = route.NextStep(target);
            if (next is not { } n)
                return new GpsInstruction(null, target.Name, target.Act, $"Act {target.Act} · {target.Name} — campaign route complete");
            wantName = n.Name; wantCode = n.Zone; exitHint = target.ExitHint;
        }
        else
        {
            wantName = target.Name; wantCode = target.Zone; exitHint = target.Name;
        }

        var (exitId, exitName) = PickExit(route, landmarks, entities, player, exitHint, wantCode);
        var via = exitName is { Length: > 0 } ? $" · take the {exitName} exit" : "";
        return new GpsInstruction(exitId, wantName ?? target.Name, target.Act, $"Act {target.Act} · → {wantName}{via}");
    }

    // Precedence: (1) landmark whose CuratedName == exitHint; (2) landmark whose CuratedName resolves
    // (via route.CodeForName) to wantCode; (3) nearest Transition entity; (4) none.
    private static (string? id, string? name) PickExit(CampaignRoute route,
        IReadOnlyList<Poe2Live.Landmark> landmarks, IReadOnlyList<Poe2Live.EntityDot> entities, V2 player,
        string? exitHint, string? wantCode)
    {
        foreach (var lm in landmarks)
            if (exitHint != null && string.Equals(lm.CuratedName, exitHint, StringComparison.OrdinalIgnoreCase))
                return ($"t:{lm.Key}", lm.CuratedName);

        foreach (var lm in landmarks)
            if (lm.CuratedName is { } cn && wantCode != null
                && string.Equals(route.CodeForName(cn), wantCode, StringComparison.OrdinalIgnoreCase))
                return ($"t:{lm.Key}", cn);

        Poe2Live.EntityDot? nearest = null; var bestSq = float.MaxValue;
        foreach (var en in entities)
        {
            if (en.Category != Poe2Live.EntityCategory.Transition) continue;
            var dsq = V2.DistanceSquared(en.Grid, player);
            if (dsq < bestSq) { bestSq = dsq; nearest = en; }
        }
        if (nearest is { } e) return ($"e:{e.Id}", null);

        return (null, null);
    }
}
