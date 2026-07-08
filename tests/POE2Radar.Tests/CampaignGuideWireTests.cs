using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using POE2Radar.Core.Campaign.Guide;
using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Web;
using Xunit;
using V2 = System.Numerics.Vector2;

namespace POE2Radar.Tests;

/// <summary>
/// EC2-WIRE contract tests. Locks in the additive wire-format shape, the volatile-publish discipline
/// on <c>RadarApp._campaignGuide</c>, and the end-to-end cursor advance path that the world thread
/// drives through <c>CampaignReconcile</c>. RadarApp itself is not spun up here (it needs a live
/// PoE2 process); instead the tests exercise the same pieces the wire path uses — RouteCursor +
/// IWorldState — via a hand-rolled test double, and verify the RadarApp field shape via reflection
/// so the boxed-Nullable publish contract can't silently regress.
/// </summary>
public class CampaignGuideWireTests
{
    // ─── v0.20 wire-format compat: RadarState.Empty still constructs positionally (13 args) ───

    [Fact]
    public void RadarState_Empty_still_constructs_and_CampaignGuide_defaults_to_null()
    {
        // If CampaignGuide were appended as a non-defaulted positional param, RadarState.Empty at
        // ApiServer.cs:2440 wouldn't compile. This test is the canary: v0.20.x wire arity intact.
        var empty = RadarState.Empty;
        Assert.Null(empty.CampaignGuide);
        Assert.Null(empty.CampaignGps);   // sibling additive field, unchanged
    }

    [Fact]
    public void RadarState_carries_CampaignGuide_as_additive_field_after_RpmPerSec()
    {
        // Positional order matters for wire-format stability. CampaignGuide must live AFTER RpmPerSec
        // on the record — otherwise a v0.20 client deserializing positionally would parse it into the
        // wrong slot. Verified via the record's primary-ctor parameter list order.
        var ctor = typeof(RadarState).GetConstructors()[0];
        var pars = ctor.GetParameters();
        var rpmIdx = Array.FindIndex(pars, p => p.Name == "RpmPerSec");
        var guideIdx = Array.FindIndex(pars, p => p.Name == "CampaignGuide");
        Assert.True(rpmIdx >= 0, "RpmPerSec must exist on RadarState");
        Assert.True(guideIdx > rpmIdx, "CampaignGuide must be positioned AFTER RpmPerSec");
        Assert.Equal(pars.Length - 1, guideIdx);   // and last (append, not insert)
        Assert.Equal(typeof(CampaignStepInstruction?), pars[guideIdx].ParameterType);
        Assert.True(pars[guideIdx].HasDefaultValue);
        Assert.Null(pars[guideIdx].DefaultValue);   // default = null so existing callers keep working
    }

    [Fact]
    public void RadarState_with_expression_populates_CampaignGuide()
    {
        var inst = new CampaignStepInstruction(
            StepId: "act1.beira",
            Text: "Kill Beira of the Rotten Pack",
            AreaId: "G1_1",
            Act: 1,
            Ordinal: 3,
            TotalSteps: 250,
            Optional: false,
            Stalled: false,
            Available: true,
            DegradationReason: null);
        var populated = RadarState.Empty with { CampaignGuide = inst };
        Assert.NotNull(populated.CampaignGuide);
        Assert.Equal("Kill Beira of the Rotten Pack", populated.CampaignGuide!.Value.Text);
        Assert.Equal(3, populated.CampaignGuide!.Value.Ordinal);
        Assert.False(populated.CampaignGuide!.Value.Stalled);
    }

    // ─── Immutability discipline (verify-gate wording, spec §12) ───

    [Fact]
    public void CampaignStepInstruction_is_immutable_readonly_record_struct()
    {
        var t = typeof(CampaignStepInstruction);
        Assert.True(t.IsValueType, "spec §12: current step must be exposed as an immutable RECORD STRUCT");
        // A `readonly record struct` compiles all instance fields as InitOnly + emits [IsReadOnlyAttribute]
        // on the type. Both signals are checked so a future refactor can't silently drop `readonly` and
        // reintroduce torn-read risk on the SSE side.
        Assert.NotNull(t.GetCustomAttribute<IsReadOnlyAttribute>());
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            Assert.True(f.IsInitOnly, $"backing field {f.Name} must be init-only (readonly record struct)");
    }

    [Fact]
    public void RouteSchemaMismatchException_is_public_Exception_type_so_ctor_catch_compiles()
    {
        // RadarApp's ctor wraps RouteModel.LoadEmbedded() in try/catch(RouteSchemaMismatchException). If
        // the type were internal or non-Exception, that catch wouldn't compile — and a schema break at
        // startup would crash the app instead of gracefully degrading (Risk #10).
        Assert.True(typeof(RouteSchemaMismatchException).IsPublic);
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(RouteSchemaMismatchException)));
    }

    // ─── SSE payload wire round-trip ───

    [Fact]
    public void SSE_payload_omits_CampaignGuide_when_null_v020_clients_see_no_change()
    {
        // With WhenWritingNull, a null CampaignGuide serializes to nothing — v0.20.x clients receive
        // the same-shaped JSON they always did. Risk #6 (wire-format compat) discharged automatically.
        var opts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.Serialize(RadarState.Empty, opts);
        Assert.DoesNotContain("CampaignGuide", json);
    }

    [Fact]
    public void SSE_payload_serializes_CampaignGuide_alongside_CampaignGps_when_both_populated()
    {
        // ADDITIVE alongside — CampaignGps stays exactly where it was, CampaignGuide appears as a
        // sibling key. Both keys must be present and independently serialized.
        var opts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var inst = new CampaignStepInstruction(
            StepId: "act1.devourer", Text: "Slay the Devourer", AreaId: "G1_2",
            Act: 1, Ordinal: 7, TotalSteps: 250, Optional: false,
            Stalled: true, Available: false,
            DegradationReason: "you're in G1_1, route expects G1_2");
        var state = RadarState.Empty with { CampaignGps = "Head to Ogham Village", CampaignGuide = inst };
        var json = JsonSerializer.Serialize(state, opts);
        Assert.Contains("\"CampaignGps\":\"Head to Ogham Village\"", json);
        Assert.Contains("\"CampaignGuide\"", json);
        Assert.Contains("\"Slay the Devourer\"", json);
        Assert.Contains("\"Stalled\":true", json);
        // "DegradationReason" key present + non-null value (default HTML-safe escaping mangles the
        // apostrophe in the payload, so match a shape-only assertion instead of the raw string).
        Assert.Contains("\"DegradationReason\":\"", json);
        Assert.Contains("route expects G1_2", json);
    }

    // ─── RadarApp publish-field discipline (mirror of _campaignGps) ───

    [Fact]
    public void RadarApp_campaignGuide_field_is_volatile_object_reference()
    {
        // Publish under the same volatile-write discipline as _campaignGps: `private volatile object?
        // _campaignGuide`. object gives us reference-swap atomicity for a boxed record struct; volatile
        // gives us the release-store the SSE thread needs. Any refactor that drops volatile or changes
        // the type breaks the parent brief's non-negotiable "mirror the _campaignGps pattern" gate.
        var f = typeof(RadarApp).GetField("_campaignGuide",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        Assert.Equal(typeof(object), f!.FieldType);
        var mods = f.GetRequiredCustomModifiers();
        Assert.Contains(typeof(IsVolatile), mods);   // this is exactly how the C# `volatile` keyword surfaces
    }

    [Fact]
    public void RadarApp_campaignGps_and_campaignGuide_share_the_publish_discipline()
    {
        // Cross-check: both fields must be volatile so the SSE reader sees release-stored writes.
        var gps = typeof(RadarApp).GetField("_campaignGps", BindingFlags.Instance | BindingFlags.NonPublic);
        var gde = typeof(RadarApp).GetField("_campaignGuide", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gps);
        Assert.NotNull(gde);
        Assert.Contains(typeof(IsVolatile), gps!.GetRequiredCustomModifiers());
        Assert.Contains(typeof(IsVolatile), gde!.GetRequiredCustomModifiers());
    }

    [Fact]
    public void RadarApp_campaignGuideAvailable_is_readonly_bool_ctor_only_flag()
    {
        var f = typeof(RadarApp).GetField("_campaignGuideAvailable",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        Assert.Equal(typeof(bool), f!.FieldType);
        Assert.True(f.IsInitOnly, "_campaignGuideAvailable must be readonly — set at ctor, never mutated");
    }

    [Fact]
    public void RadarApp_holds_adapter_and_cursor_as_readonly_ctor_owned_fields()
    {
        // Ctor-owned, never reassigned — the runtime toggle path just checks their null-ness and
        // reads their published state. Prevents accidental swap on a background thread.
        foreach (var name in new[] { "_worldStateAdapter", "_routeCursor" })
        {
            var f = typeof(RadarApp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            Assert.True(f!.IsInitOnly, $"{name} must be readonly (ctor-owned)");
        }
    }

    // ─── End-to-end wire path (adapter + cursor + volatile publish + JSON), no RadarApp needed ───

    [Fact]
    public void Cursor_Tick_advances_on_satisfied_step_and_publishes_immutable_snapshot()
    {
        // Mirrors what CampaignReconcile does per tick: Refresh a world snapshot, Tick the cursor,
        // read CurrentInstruction, publish. Uses a tiny two-step route + a fake IWorldState so the
        // advance actually fires without touching a live PoE2 process.
        var step0 = MakeEnterAreaStep("s0", areaId: "G1_1", targetAreaId: "G1_2");
        var step1 = MakeEnterAreaStep("s1", areaId: "G1_2", targetAreaId: "G1_3");
        var route = new RouteModel(RouteModel.CurrentVersion, new[] { step0, step1 });
        var cursor = new RouteCursor(route);

        // Step 0 satisfied → cursor advances to step 1 → publication swaps.
        var world = new FakeWorld { AreaMatch = "G1_2" };
        cursor.Tick(world);

        Assert.Equal(1, cursor.CurrentOrdinal);
        var snap = cursor.CurrentInstruction;
        Assert.NotNull(snap);
        Assert.Equal("s1", snap!.Value.StepId);
        Assert.Equal(1, snap.Value.Ordinal);
        Assert.Equal(2, snap.Value.TotalSteps);
    }

    [Fact]
    public void Area_change_forward_snap_moves_cursor_past_preceding_steps()
    {
        // Spec §6 graceful degradation: on area-change, snap the cursor forward to the first step whose
        // AreaId matches the new area — never rewinds. Locks in the ordering CampaignReconcile depends
        // on (OnAreaChange runs BEFORE Tick each tick where the area changed).
        var route = new RouteModel(RouteModel.CurrentVersion, new[]
        {
            MakeEnterAreaStep("s0", areaId: "G1_1", targetAreaId: "G1_2"),
            MakeEnterAreaStep("s1", areaId: "G1_2", targetAreaId: "G1_3"),
            MakeEnterAreaStep("s2", areaId: "G1_3", targetAreaId: "G1_4"),
        });
        var cursor = new RouteCursor(route);
        Assert.Equal(0, cursor.CurrentOrdinal);

        cursor.OnAreaChange("G1_3");
        Assert.Equal(2, cursor.CurrentOrdinal);
        Assert.Equal("s2", cursor.CurrentInstruction!.Value.StepId);
    }

    [Fact]
    public void Boxed_publish_pattern_roundtrips_through_object_slot_to_Nullable()
    {
        // Simulates the CampaignReconcile write + SSE payload read: box the Nullable<T>, store as
        // object, pattern-match unbox. Two shapes: populated (unbox succeeds) and null (unbox fails,
        // surface as null). Locks in the SSE-side `is CampaignStepInstruction __cg ? ...` idiom.
        object? slot;
        CampaignStepInstruction? populated = new CampaignStepInstruction(
            "sX", "walk to town", "G1_1", 1, 0, 1, false, false, true, null);
        slot = (object?)populated;
        Assert.True(slot is CampaignStepInstruction);
        var unboxed = slot is CampaignStepInstruction cg ? cg : (CampaignStepInstruction?)null;
        Assert.NotNull(unboxed);
        Assert.Equal("walk to town", unboxed!.Value.Text);

        CampaignStepInstruction? absent = null;
        slot = (object?)absent;
        Assert.Null(slot);
        var unboxedNull = slot is CampaignStepInstruction cg2 ? cg2 : (CampaignStepInstruction?)null;
        Assert.Null(unboxedNull);
    }

    // ─── Fixture helpers ────────────────────────────────────────────────────────

    private static RouteStep MakeEnterAreaStep(string id, string areaId, string targetAreaId) =>
        new(
            Id: id,
            Act: 1,
            AreaId: areaId,
            AreaName: areaId,
            Text: $"Enter {targetAreaId}",
            Note: "",
            Optional: false,
            CompleteWhen: CompleteWhen.All,
            Objectives: new[]
            {
                new Objective(
                    Type: ObjectiveType.EnterArea,
                    AreaTarget: new Pattern(targetAreaId)),
            },
            ImportFp: null);

    private sealed class FakeWorld : IWorldState
    {
        public string AreaMatch { get; set; } = "";
        public bool InAreaSatisfied(Pattern area) =>
            !string.IsNullOrEmpty(AreaMatch) && !string.IsNullOrEmpty(area.Value) &&
            AreaMatch.Contains(area.Value, StringComparison.OrdinalIgnoreCase);
        public bool QuestFlagSatisfied(Pattern flag) => false;
        public bool WaypointPulsed() => false;
        public bool ProximitySatisfied(IReadOnlyList<EntityMatcher>? entities, IReadOnlyList<Pattern>? tiles, float distance) => false;
        public bool LootSatisfied(IReadOnlyList<ItemMatcher>? items) => false;
        public int KillProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int InteractProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int TalkProgress(IReadOnlyList<EntityMatcher>? entities) => 0;
        public int SatisfiedFlagCount(IReadOnlyList<Pattern> flags) => 0;
    }
}
