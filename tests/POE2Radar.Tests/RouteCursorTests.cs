using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using POE2Radar.Core.Campaign.Guide;
using Xunit;

namespace POE2Radar.Tests;

public sealed class RouteCursorTests
{
    // ------------------------------------------------------------ fixtures

    // Fabricates a RouteStep with the 10-field ctor filled in from safe defaults, so tests only
    // need to spell out the fields that matter to the assertion at hand.
    private static RouteStep MakeStep(
        string id,
        string areaId,
        string text,
        params Objective[] objectives) =>
        new(
            Id: id,
            Act: 1,
            AreaId: areaId,
            AreaName: areaId,
            Text: text,
            Note: "",
            Optional: false,
            CompleteWhen: CompleteWhen.All,
            Objectives: objectives,
            ImportFp: null);

    private static Objective EnterArea(string areaId) =>
        new(Type: ObjectiveType.EnterArea, AreaTarget: new Pattern(areaId));

    private static Objective KillEntity(string namePattern) =>
        new(
            Type: ObjectiveType.Kill,
            Entities: new[] { new EntityMatcher(new Pattern(namePattern)) });

    private static Objective TalkTo(string npc) =>
        new(
            Type: ObjectiveType.Talk,
            Entities: new[] { new EntityMatcher(new Pattern(npc)) });

    private static RouteModel Route(params RouteStep[] steps) =>
        new(Version: RouteModel.CurrentVersion, Steps: steps);

    // Minimal IWorldState fake: the four v0.21 live signals are toggleable via public knobs; the
    // five stubs return false/0 exactly like WorldStateAdapter does in production.
    private sealed class FakeWorld : IWorldState
    {
        public string CurrentAreaCode = "";

        public bool InAreaSatisfied(Pattern area)
        {
            if (area is null || string.IsNullOrEmpty(area.Value)) return false;
            return string.Equals(area.Value, CurrentAreaCode, StringComparison.OrdinalIgnoreCase);
        }
        public bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities,
            IReadOnlyList<Pattern>? tiles, float distance) => false;
        public int KillProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items) => false;

        // Task-3 stubs — production-parity.
        public bool QuestFlagSatisfied(Pattern flag) => false;
        public bool WaypointPulsed() => false;
        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags) => 0;
        public int TalkProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
    }

    // ------------------------------------------------------------ smoke

    [Fact]
    public void EmptyRoute_PublishesNullInstruction()
    {
        var cursor = new RouteCursor(Route());

        Assert.Equal(0, cursor.CurrentOrdinal);
        Assert.Null(cursor.CurrentInstruction);
    }

    [Fact]
    public void ConstructedRoute_PublishesFirstStepAtOrdinalZero()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "Enter the Riverside", EnterArea("G1_1")),
            MakeStep("s1", "G1_1", "Kill Beira",           KillEntity("Beira"))));

        Assert.Equal(0, cursor.CurrentOrdinal);
        var ins = cursor.CurrentInstruction;
        Assert.NotNull(ins);
        Assert.Equal("s0", ins!.Value.StepId);
        Assert.Equal("G1_1", ins.Value.AreaId);
        Assert.Equal(0, ins.Value.Ordinal);
        Assert.Equal(2, ins.Value.TotalSteps);
    }

    [Fact]
    public void NullRoute_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RouteCursor(null!));
    }

    // ------------------------------------------------------------ tick advance

    [Fact]
    public void Tick_AdvancesOrdinal_WhenCurrentStepSatisfied()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "Enter the Riverside", EnterArea("G1_1")),
            MakeStep("s1", "G1_1", "Kill Beira",           KillEntity("Beira"))));
        // Step 0 (EnterArea G1_1) satisfies from CurrentAreaCode; step 1 (Kill Beira, count=1)
        // stays unsatisfied because NextKillCount is 0.
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };

        cursor.Tick(world);

        Assert.Equal(1, cursor.CurrentOrdinal);
        Assert.Equal("s1", cursor.CurrentInstruction?.StepId);
        Assert.False(cursor.CurrentInstruction?.Stalled ?? true);
    }

    [Fact]
    public void Tick_WalksPastMultipleSatisfiedStepsInOneCall()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1")),
            MakeStep("s1", "G1_1", "step 1", EnterArea("G1_1")),
            MakeStep("s2", "G1_1", "step 2", EnterArea("G1_1")),
            MakeStep("s3", "G1_1", "step 3", KillEntity("Beira"))));
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };

        cursor.Tick(world);

        Assert.Equal(3, cursor.CurrentOrdinal);
        Assert.Equal("s3", cursor.CurrentInstruction?.StepId);
    }

    [Fact]
    public void Tick_StopsAtFirstUnsatisfiedStep_LeavesOrdinalMonotonic()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1")),
            MakeStep("s1", "G1_1", "step 1", KillEntity("Beira")),  // stops here
            MakeStep("s2", "G1_1", "step 2", EnterArea("G1_1"))));
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };

        cursor.Tick(world);
        Assert.Equal(1, cursor.CurrentOrdinal);
        cursor.Tick(world);
        Assert.Equal(1, cursor.CurrentOrdinal);   // no rewind, no forward jump past the stall
    }

    [Fact]
    public void Tick_OnCompletedRoute_IsZeroCost_NoRepublishNoThrow()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "only", EnterArea("G1_1"))));
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };

        cursor.Tick(world);   // completes
        Assert.Null(cursor.CurrentInstruction);
        // Post-completion, another tick must be a no-op — must not throw, must not resurrect a
        // snapshot.
        var before = cursor.CurrentInstruction;
        cursor.Tick(world);
        Assert.Equal(before, cursor.CurrentInstruction);
    }

    [Fact]
    public void Tick_WithNullWorld_IsNoOp()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1"))));

        cursor.Tick(null!);   // must not throw
        Assert.Equal(0, cursor.CurrentOrdinal);
    }

    // ------------------------------------------------------------ area-change forward-snap

    [Fact]
    public void OnAreaChange_ForwardSnapsPastStalledSteps_ToFirstStepInNewArea()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1",   "Riverside",       EnterArea("G1_1")),
            MakeStep("s1", "G1_1",   "Talk to NPC",     TalkTo("Renly")),   // stubbed talk = stalled
            MakeStep("s2", "G1_2_1", "Enter Clearfell", EnterArea("G1_2_1")),
            MakeStep("s3", "G1_2_1", "Kill Beira",      KillEntity("Beira"))));
        Assert.Equal(0, cursor.CurrentOrdinal);

        cursor.OnAreaChange("G1_2_1");

        Assert.Equal(2, cursor.CurrentOrdinal);
        Assert.Equal("s2", cursor.CurrentInstruction?.StepId);
        Assert.False(cursor.CurrentInstruction?.Stalled ?? true);
    }

    [Fact]
    public void OnAreaChange_MarksStalled_WhenNoStepInNewAreaAhead()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "Riverside", EnterArea("G1_1"))));

        cursor.OnAreaChange("G1_TOWN");   // town side-trip — no route step here

        Assert.Equal(0, cursor.CurrentOrdinal);           // no rewind, no forward jump
        Assert.True(cursor.CurrentInstruction?.Stalled);
        Assert.NotNull(cursor.CurrentInstruction?.DegradationReason);
    }

    [Fact]
    public void OnAreaChange_DoesNotRewind_WhenNewAreaIsBehindCursor()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1",   "step 0", EnterArea("G1_1")),
            MakeStep("s1", "G1_2_1", "step 1", EnterArea("G1_2_1"))));
        // Force cursor to step 1.
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };
        cursor.Tick(world);
        Assert.Equal(1, cursor.CurrentOrdinal);

        cursor.OnAreaChange("G1_1");   // player back-tracked to an earlier area

        Assert.Equal(1, cursor.CurrentOrdinal);   // cursor pinned forward
        Assert.True(cursor.CurrentInstruction?.Stalled);   // step.AreaId != current area
    }

    [Fact]
    public void OnAreaChange_SameAreaTwice_IsNoOp()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1"))));

        cursor.OnAreaChange("G1_1");
        var first = cursor.CurrentInstruction;
        cursor.OnAreaChange("G1_1");   // idempotent
        var second = cursor.CurrentInstruction;
        Assert.Equal(first, second);
    }

    [Fact]
    public void OnAreaChange_EmptyOrNullString_IsNoOp()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1"))));

        cursor.OnAreaChange("");
        cursor.OnAreaChange(null!);
        Assert.Equal(0, cursor.CurrentOrdinal);
    }

    // ------------------------------------------------------------ CampaignStepInstruction shape

    [Fact]
    public void CurrentInstruction_ExposesFullCanonicalShape()
    {
        var route = Route(
            MakeStep("s0", "G1_1", "Enter the Riverside", EnterArea("G1_1")),
            MakeStep("s1", "G1_1", "Kill Beira",           KillEntity("Beira")));
        var cursor = new RouteCursor(route);

        var ins = cursor.CurrentInstruction!.Value;
        Assert.Equal("s0", ins.StepId);
        Assert.Equal("Enter the Riverside", ins.Text);
        Assert.Equal("G1_1", ins.AreaId);
        Assert.Equal(1, ins.Act);
        Assert.Equal(0, ins.Ordinal);
        Assert.Equal(2, ins.TotalSteps);
        Assert.False(ins.Optional);
        Assert.False(ins.Stalled);
        Assert.True(ins.Available);
        Assert.Null(ins.DegradationReason);
    }

    [Fact]
    public void StubbedTalkStep_MarksStalled_WithDegradationReason()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "Talk to Renly", TalkTo("Renly"))));
        var world = new FakeWorld { CurrentAreaCode = "G1_1" };
        cursor.OnAreaChange("G1_1");   // align current area to step's area

        cursor.Tick(world);   // Talk stub returns 0 → step doesn't advance

        Assert.Equal(0, cursor.CurrentOrdinal);
        Assert.True(cursor.CurrentInstruction?.Stalled);
        Assert.False(cursor.CurrentInstruction?.Available);
        Assert.NotNull(cursor.CurrentInstruction?.DegradationReason);
    }

    [Fact]
    public void Snapshot_IsImmutable_AndValueEquatable()
    {
        var cursor = new RouteCursor(Route(
            MakeStep("s0", "G1_1", "step 0", EnterArea("G1_1"))));

        var a = cursor.CurrentInstruction;
        var b = cursor.CurrentInstruction;
        Assert.Equal(a, b);   // record-struct value equality
    }

    // Alternating-satisfy world: every other call to InAreaSatisfied returns true, so the Tick's
    // internal while-loop advances the cursor by exactly one step per Tick call. That gives the SSE
    // reader a real per-step observation window during the stress drain instead of collapsing the
    // whole route into a single tick.
    private sealed class OneStepPerTickWorld : IWorldState
    {
        private int _callCounter;
        public bool InAreaSatisfied(Pattern area)
            => (Interlocked.Increment(ref _callCounter) & 1) == 1;

        public bool QuestFlagSatisfied(Pattern flag) => false;
        public bool WaypointPulsed() => false;
        public bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities,
            IReadOnlyList<Pattern>? tiles, float distance) => false;
        public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items) => false;
        public int KillProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int TalkProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags) => 0;
    }

    // ------------------------------------------------------------ verify gate — concurrency stress

    [Fact]
    public void ConcurrencyStress_TickAndSnapshotRead_NoTornReadsAndOrdinalMonotonicWithinArea()
    {
        // 200-step single-area route, one EnterArea objective per step. The OneStepPerTickWorld
        // alternator ensures each Tick advances exactly one ordinal — without that, one Tick would
        // drain the whole route before the SSE thread even scheduled, leaving zero observable
        // snapshots.
        const string Area = "G1_STRESS";
        var steps = new List<RouteStep>();
        for (var i = 0; i < 200; i++) steps.Add(MakeStep($"s{i}", Area, $"step {i}", EnterArea(Area)));
        var cursor = new RouteCursor(new RouteModel(RouteModel.CurrentVersion, steps));
        var world  = new OneStepPerTickWorld();

        var stop = 0;
        var lastOrdinal = -1;
        var tornRead = 0;
        var mismatchedStepId = 0;
        var readerOrdinals = new ConcurrentBag<int>();

        var worldThread = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                cursor.Tick(world);
                // No break on completion — world thread keeps ticking (no-op) so both threads stay
                // busy the whole stress window. Per-tick SpinWait gives the SSE reader real CPU
                // time during the drain phase (~200 * spin ≈ dozens of ms of observation window).
                Thread.SpinWait(10_000);
            }
        }) { IsBackground = true };

        var sseThread = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var ins = cursor.CurrentInstruction;
                if (ins is { } snap)
                {
                    // Torn read: StepId embedded in the snapshot must always agree with its
                    // Ordinal, since Build/Publish never mixes fields across two steps.
                    if (snap.StepId != $"s{snap.Ordinal}") Interlocked.Exchange(ref mismatchedStepId, 1);
                    if (snap.Ordinal < lastOrdinal) Interlocked.Exchange(ref tornRead, 1);
                    lastOrdinal = snap.Ordinal;
                    readerOrdinals.Add(snap.Ordinal);
                }
            }
        }) { IsBackground = true };

        try
        {
            worldThread.Start();
            sseThread.Start();
            // Verify-gate stress budget: 3s tight loop (matches the pattern that caught v0.20.1's
            // heartbeat race). Alternating satisfy + SpinWait keeps drain ~30-100ms, so the SSE
            // reader has a real window to catch every-ordinal snapshots.
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            worldThread.Join();
            sseThread.Join();
        }

        Assert.Equal(0, Volatile.Read(ref mismatchedStepId));   // no torn StepId/Ordinal pair
        Assert.Equal(0, Volatile.Read(ref tornRead));           // ordinal never regressed within the run
        Assert.NotEmpty(readerOrdinals);                        // reader observed live snapshots
        Assert.Equal(steps.Count, cursor.CurrentOrdinal);       // world thread drained to completion
    }
}
