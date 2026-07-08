using System;
using System.Collections.Generic;
using POE2Radar.Core.Campaign.Guide;
using Xunit;

namespace POE2Radar.Tests;

// EC2-TEST verify-gate coverage for the AdvanceEngine (static) — per-signal advance behavior against a
// FakeWorldState that returns hard-false/0 for every v0.22-stubbed IWorldState signal. The gate assertion
// (spec §12) is Stubbed_signal_never_satisfies_a_step: with a fully "satisfied-looking" fake world, the
// engine still refuses to advance objectives whose IWorldState signal is stubbed for v0.21.
public sealed class AdvanceEngineTests
{
    // ------------------------------------------------------------ fixtures

    // Minimal IWorldState fake. Live signals (InArea, Proximity, Kill, Loot-CARRY) expose per-test knobs;
    // the four v0.22-pending stubs plus SatisfiedFlagCount always return false/0 with no override hook —
    // that's the whole point of the EC2-TEST verify gate.
    private sealed class FakeWorld : IWorldState
    {
        public string CurrentAreaCode { get; set; } = "";
        public Dictionary<string, int> Kills { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LiveLoot { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (bool hit, float distance)? ProximityAnswer { get; set; }

        // LIVE signals — settable per test.
        public bool InAreaSatisfied(Pattern area)
            => area is { Value: var v } && !string.IsNullOrEmpty(v)
               && string.Equals(v, CurrentAreaCode, StringComparison.OrdinalIgnoreCase);

        public bool ProximitySatisfied(
            IReadOnlyList<EntityMatcher>? entities,
            IReadOnlyList<Pattern>? tiles,
            float distance)
            => ProximityAnswer is { hit: true, distance: var d } && Math.Abs(d - distance) < 0.001f;

        public int KillProgress(IReadOnlyList<EntityMatcher>? entities)
        {
            if (entities is null || entities.Count == 0) return 0;
            var key = entities[0].Match.Value;
            return Kills.TryGetValue(key, out var n) ? n : 0;
        }

        public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items)
        {
            if (items is null || items.Count == 0) return false;
            foreach (var i in items)
                if (!LiveLoot.Contains(i.Match.Value)) return false;
            return true;
        }

        // STUBBED signals — hard false/0 with no override hook. The whole point of the verify gate.
        public bool QuestFlagSatisfied(Pattern flag) => false;
        public bool WaypointPulsed() => false;
        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags) => 0;
        public int TalkProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
    }

    // A "satisfied-looking" fake: everything a live signal could care about is set to true/present. Used
    // by the stubbed-type theory to prove the stub short-circuits the objective regardless of what the
    // live signals would say.
    private static FakeWorld FullyLoadedWorld()
    {
        var w = new FakeWorld
        {
            CurrentAreaCode = "A1",
            ProximityAnswer = (hit: true, distance: AdvanceEngine.DefaultProximity),
        };
        w.Kills["anything"] = 999;
        w.LiveLoot.Add("anything");
        return w;
    }

    private static RouteStep StepOf(CompleteWhen when, params Objective[] objs) =>
        new(
            Id: "s",
            Act: 1,
            AreaId: "A1",
            AreaName: "A1",
            Text: "t",
            Note: "",
            Optional: false,
            CompleteWhen: when,
            Objectives: objs,
            ImportFp: null);

    private static RouteStep StepOf(params Objective[] objs) => StepOf(CompleteWhen.All, objs);

    // Objective builders — keep the tests focused on the assertion, not the record ctor.
    private static Objective Kill(string name, int count = 1, IReadOnlyList<Pattern>? flags = null) =>
        new(
            Type: ObjectiveType.Kill,
            Entities: new[] { new EntityMatcher(new Pattern(name)) },
            Count: count,
            ProgressFlags: flags);

    private static Objective Interact(string name) =>
        new(ObjectiveType.Interact, Entities: new[] { new EntityMatcher(new Pattern(name)) });

    private static Objective Talk(string name) =>
        new(ObjectiveType.Talk, Entities: new[] { new EntityMatcher(new Pattern(name)) });

    private static Objective Loot(string name) =>
        new(ObjectiveType.Loot, Items: new[] { new ItemMatcher(new Pattern(name)) });

    private static Objective Proximity(string entity, float distance = 0f) =>
        new(
            ObjectiveType.Proximity,
            Entities: new[] { new EntityMatcher(new Pattern(entity)) },
            Distance: distance);

    private static Objective QuestFlag(string flag) =>
        new(ObjectiveType.QuestFlag, Flag: new Pattern(flag));

    private static Objective EnterArea(string areaId) =>
        new(ObjectiveType.EnterArea, AreaTarget: new Pattern(areaId));

    private static Objective ActivateWaypoint() =>
        new(ObjectiveType.ActivateWaypoint);

    private static Objective Manual() =>
        new(ObjectiveType.Manual);

    // ------------------------------------------------------------ LIVE: EnterArea

    [Fact]
    public void EnterArea_advances_when_current_area_matches_area_target()
    {
        var step = StepOf(EnterArea("G1_2"));
        var ws   = new FakeWorld { CurrentAreaCode = "G1_2" };

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
        Assert.True(AdvanceEngine.ObjectiveSatisfied(step.Objectives[0], ws));
    }

    [Fact]
    public void EnterArea_stalls_when_current_area_differs()
    {
        var step = StepOf(EnterArea("G1_2"));
        var ws   = new FakeWorld { CurrentAreaCode = "G1_town" };

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void EnterArea_with_null_area_target_never_satisfies()
    {
        var step = StepOf(new Objective(ObjectiveType.EnterArea, AreaTarget: null));
        var ws   = new FakeWorld { CurrentAreaCode = "G1_2" };

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    // ------------------------------------------------------------ LIVE: Proximity

    [Fact]
    public void Proximity_uses_default_distance_when_objective_distance_is_zero()
    {
        // AdvanceEngine.DefaultProximity = 40f; objective Distance=0 must upgrade to that default.
        var step = StepOf(Proximity("Boss", distance: 0f));
        var ws   = new FakeWorld { ProximityAnswer = (hit: true, distance: AdvanceEngine.DefaultProximity) };

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Proximity_uses_explicit_distance_when_objective_distance_is_positive()
    {
        var step = StepOf(Proximity("Boss", distance: 12.5f));
        var ws   = new FakeWorld { ProximityAnswer = (hit: true, distance: 12.5f) };

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Proximity_stalls_when_signal_returns_no_hit()
    {
        var step = StepOf(Proximity("Boss"));
        var ws   = new FakeWorld { ProximityAnswer = null };

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    // ------------------------------------------------------------ LIVE: Kill

    [Fact]
    public void Kill_advances_when_live_kill_count_reaches_needed()
    {
        var step = StepOf(Kill("Beira", count: 1));
        var ws   = new FakeWorld();
        ws.Kills["Beira"] = 1;

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Kill_advances_when_live_count_exceeds_needed()
    {
        var step = StepOf(Kill("Beira", count: 3));
        var ws   = new FakeWorld();
        ws.Kills["Beira"] = 10;

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Kill_stalls_when_live_count_below_needed()
    {
        var step = StepOf(Kill("Beira", count: 3));
        var ws   = new FakeWorld();
        ws.Kills["Beira"] = 2;

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Kill_count_zero_is_treated_as_one_needed()
    {
        // AdvanceEngine.CountDone: needed = Count > 0 ? Count : 1. A missing/zero Count MUST NOT collapse
        // to "always satisfied" — it defaults to 1 kill.
        var stepZero = StepOf(Kill("Beira", count: 0));
        var wsNone   = new FakeWorld();
        Assert.False(AdvanceEngine.IsStepSatisfied(stepZero, wsNone));

        var wsOne = new FakeWorld();
        wsOne.Kills["Beira"] = 1;
        Assert.True(AdvanceEngine.IsStepSatisfied(stepZero, wsOne));
    }

    [Fact]
    public void Kill_with_progress_flags_routes_through_stubbed_SatisfiedFlagCount()
    {
        // Kill/Interact/Talk objectives with ProgressFlags populated intentionally bypass live counters
        // and read SatisfiedFlagCount instead (the 3-obelisk pattern from ExileCampaigns2). That signal
        // is v0.22-stubbed → the objective stays unsatisfied even when live kill count is huge.
        var step = StepOf(Kill("Beira", count: 1, flags: new[] { new Pattern("obelisk_1") }));
        var ws   = new FakeWorld();
        ws.Kills["Beira"] = 999;   // live count irrelevant; ProgressFlags wins

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    // ------------------------------------------------------------ LIVE: Loot

    [Fact]
    public void Loot_advances_when_all_items_present_in_carry_inventory()
    {
        var step = StepOf(Loot("MedicinalHerb"));
        var ws   = new FakeWorld();
        ws.LiveLoot.Add("MedicinalHerb");

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void Loot_stalls_when_no_matching_item_in_inventory()
    {
        var step = StepOf(Loot("MedicinalHerb"));
        var ws   = new FakeWorld();
        ws.LiveLoot.Add("SomethingElse");

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    // ------------------------------------------------------------ STUBBED (v0.22) — always false

    [Fact]
    public void QuestFlag_never_advances_because_signal_is_v022_stubbed()
    {
        var step = StepOf(QuestFlag("SamuelZombieAttackSeen"));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void QuestFlag_with_null_flag_never_satisfies()
    {
        var step = StepOf(new Objective(ObjectiveType.QuestFlag, Flag: null));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void ActivateWaypoint_never_advances_because_signal_is_v022_stubbed()
    {
        var step = StepOf(ActivateWaypoint());

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void Talk_never_advances_because_signal_is_v022_stubbed()
    {
        var step = StepOf(Talk("Renly"));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void Interact_never_advances_because_signal_is_v022_stubbed()
    {
        var step = StepOf(Interact("Shrine"));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void Manual_objective_never_advances_by_design()
    {
        // Manual is not stubbed — it's an intentional "user marks complete" objective type. AdvanceEngine
        // hardcodes false; only external UI (out of scope for v0.21) will flip a step past it. Pinned
        // here so a future refactor can't silently start auto-advancing Manual steps.
        var step = StepOf(Manual());

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    // Verify-gate theory (spec §12): every ObjectiveType whose signal is v0.22-stubbed must refuse to
    // advance regardless of what any live signal on the fake would say. If this ever goes red, either
    // (a) AdvanceEngine forgot to route the type through the stubbed signal, or (b) FakeWorld leaked a
    // live answer into a stubbed method. DO NOT weaken the assertion — patch the impl.
    [Theory]
    [InlineData(ObjectiveType.QuestFlag)]
    [InlineData(ObjectiveType.ActivateWaypoint)]
    [InlineData(ObjectiveType.Talk)]
    [InlineData(ObjectiveType.Interact)]
    [InlineData(ObjectiveType.Manual)]
    public void Stubbed_signal_never_satisfies_a_step_even_when_world_is_fully_loaded(ObjectiveType t)
    {
        var objective = t switch
        {
            ObjectiveType.QuestFlag        => QuestFlag("anything"),
            ObjectiveType.ActivateWaypoint => ActivateWaypoint(),
            ObjectiveType.Talk             => Talk("anything"),
            ObjectiveType.Interact         => Interact("anything"),
            ObjectiveType.Manual           => Manual(),
            _ => throw new InvalidOperationException($"unhandled stubbed type: {t}"),
        };
        var step = StepOf(objective);

        Assert.False(
            AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()),
            $"ObjectiveType {t} routes through a v0.22-stubbed IWorldState signal and MUST NOT advance in v0.21");
    }

    // ------------------------------------------------------------ CompleteWhen: All vs. Any

    [Fact]
    public void CompleteWhen_All_advances_only_when_every_objective_satisfied()
    {
        var ws = new FakeWorld { CurrentAreaCode = "G1_1" };
        ws.Kills["Beira"] = 1;
        var step = StepOf(CompleteWhen.All, EnterArea("G1_1"), Kill("Beira"));

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void CompleteWhen_All_stalls_when_only_one_of_two_objectives_satisfied()
    {
        var ws = new FakeWorld { CurrentAreaCode = "G1_1" };
        // Kill objective unmet — live counter at 0.
        var step = StepOf(CompleteWhen.All, EnterArea("G1_1"), Kill("Beira"));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void CompleteWhen_Any_advances_when_only_one_of_two_objectives_satisfied()
    {
        // Same fake as the .All-stalls case above — differs only in CompleteWhen — proving the mode
        // switch actually moves the gate.
        var ws = new FakeWorld { CurrentAreaCode = "G1_1" };
        var step = StepOf(CompleteWhen.Any, EnterArea("G1_1"), Kill("Beira"));

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void CompleteWhen_Any_stalls_when_no_objective_satisfied()
    {
        var ws = new FakeWorld { CurrentAreaCode = "elsewhere" };
        var step = StepOf(CompleteWhen.Any, EnterArea("G1_1"), Kill("Beira"));

        Assert.False(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    [Fact]
    public void CompleteWhen_Any_ignores_stubbed_siblings_when_at_least_one_live_signal_hits()
    {
        // Realistic mixed step: live EnterArea satisfied, sibling stubbed QuestFlag never satisfies.
        // .Any must still advance on the live sibling — this is how a v0.21 stubbed-signal step still
        // clears when its author put a live fallback in.
        var ws = new FakeWorld { CurrentAreaCode = "G1_1" };
        var step = StepOf(CompleteWhen.Any, EnterArea("G1_1"), QuestFlag("stub"));

        Assert.True(AdvanceEngine.IsStepSatisfied(step, ws));
    }

    // ------------------------------------------------------------ edge cases

    [Fact]
    public void Step_with_no_objectives_is_never_satisfied()
    {
        var step = StepOf( /* empty */ );

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void Step_with_null_objectives_list_is_never_satisfied()
    {
        // Ctor guard: RouteStep.Objectives is non-nullable in the record signature but nothing stops a
        // caller from passing null at runtime. IsStepSatisfied null-guards defensively — pinned here.
        var step = new RouteStep(
            Id: "s", Act: 1, AreaId: "A1", AreaName: "A1", Text: "t", Note: "",
            Optional: false, CompleteWhen: CompleteWhen.All,
            Objectives: null!, ImportFp: null);

        Assert.False(AdvanceEngine.IsStepSatisfied(step, FullyLoadedWorld()));
    }

    [Fact]
    public void Null_step_is_never_satisfied_and_does_not_throw()
    {
        Assert.False(AdvanceEngine.IsStepSatisfied(null!, FullyLoadedWorld()));
    }
}
