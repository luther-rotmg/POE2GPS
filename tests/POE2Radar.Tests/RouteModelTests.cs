using System;
using System.Collections.Generic;
using POE2Radar.Core.Campaign.Guide;
using Xunit;

namespace POE2Radar.Tests;

// EC2-TEST verify-gate coverage for POE2Radar.Core.Campaign.Guide.RouteModel — the FromJson test seam
// (six-string overload) plus the LoadEmbedded happy path. Companion JSONs are carried through as raw
// strings, so a malformed overrides payload must NOT prevent route.json from parsing.
public sealed class RouteModelTests
{
    // ------------------------------------------------------------ fixtures

    // Wire-format is "version" (case-insensitive) per RouteModel.FromJson's private RouteDoc DTO.
    private const string GoodRoute = """
    {
      "version": 2,
      "steps": [
        {
          "id": "s0",
          "act": 1,
          "areaId": "G1_1",
          "areaName": "Riverbank",
          "text": "Enter the Riverside",
          "note": "",
          "optional": false,
          "completeWhen": "All",
          "objectives": [ { "type": "EnterArea", "areaTarget": { "value": "G1_1" } } ],
          "importFp": null
        },
        {
          "id": "s1",
          "act": 1,
          "areaId": "G1_1",
          "areaName": "Riverbank",
          "text": "Kill Beira",
          "note": "",
          "optional": false,
          "completeWhen": "All",
          "objectives": [ { "type": "Kill", "count": 1, "entities": [ { "match": { "value": "Beira" } } ] } ],
          "importFp": null
        }
      ]
    }
    """;

    private const string WrongVersion = """{ "version": 99, "steps": [] }""";
    private const string Truncated    = """{ "version": 2, "steps": [ { "id": "s0", "text": "op""";
    private const string JsonNull     = "null";
    private const string EmptyRoute   = """{ "version": 2, "steps": [] }""";

    private const string EmptyOverrides = "{}";
    private const string EmptyAreaObj   = "{}";
    private const string EmptyAreaTrs   = "{}";
    private const string EmptyAreaTgt   = "{}";
    private const string EmptyXpCurve   = """{ "cumulative": [] }""";

    private static RouteModel Load(string route, string overrides = "{}") =>
        RouteModel.FromJson(route, overrides, EmptyAreaObj, EmptyAreaTrs, EmptyAreaTgt, EmptyXpCurve);

    // ------------------------------------------------------------ LoadEmbedded happy path

    [Fact]
    public void LoadEmbedded_returns_nonempty_route_at_current_version()
    {
        var m = RouteModel.LoadEmbedded();

        Assert.Equal(RouteModel.CurrentVersion, m.Version);
        Assert.Equal(2, m.Version);
        Assert.True(m.Steps.Count > 0, "embedded route.json must expose > 0 steps");
    }

    [Fact]
    public void LoadEmbedded_carries_all_five_companion_json_payloads_as_raw_strings()
    {
        var m = RouteModel.LoadEmbedded();

        Assert.False(string.IsNullOrWhiteSpace(m.OverridesJson),       "overrides.json missing");
        Assert.False(string.IsNullOrWhiteSpace(m.AreaObjectivesJson),  "area-objectives.json missing");
        Assert.False(string.IsNullOrWhiteSpace(m.AreaTransitionsJson), "area-transitions.json missing");
        Assert.False(string.IsNullOrWhiteSpace(m.AreaTargetsJson),     "area-targets.json missing");
        Assert.False(string.IsNullOrWhiteSpace(m.XpCurveJson),         "xp_curve.json missing");
    }

    [Fact]
    public void LoadEmbedded_first_step_has_id_act_area_populated()
    {
        // Guards against a canonical route.json regression where a step lands with a blank AreaId
        // (which would silently disable RouteCursor's forward-snap). Doesn't pin exact IDs — that's
        // route content that changes between drops.
        var m = RouteModel.LoadEmbedded();
        var first = m.Steps[0];

        Assert.False(string.IsNullOrWhiteSpace(first.Id));
        Assert.False(string.IsNullOrWhiteSpace(first.AreaId));
        Assert.True(first.Act >= 1);
        Assert.NotNull(first.Objectives);
        Assert.True(first.Objectives.Count > 0);
    }

    // ------------------------------------------------------------ FromJson success cases

    [Fact]
    public void FromJson_good_route_parses_two_steps_with_typed_objectives()
    {
        var m = Load(GoodRoute);

        Assert.Equal(2, m.Steps.Count);
        Assert.Equal("s0", m.Steps[0].Id);
        Assert.Equal("s1", m.Steps[1].Id);
        Assert.Equal(ObjectiveType.EnterArea, m.Steps[0].Objectives[0].Type);
        Assert.Equal("G1_1", m.Steps[0].Objectives[0].AreaTarget!.Value);
        Assert.Equal(ObjectiveType.Kill, m.Steps[1].Objectives[0].Type);
        Assert.Equal("Beira", m.Steps[1].Objectives[0].Entities![0].Match.Value);
    }

    [Fact]
    public void FromJson_empty_steps_at_current_version_is_valid()
    {
        // v=CurrentVersion with an empty steps[] is a legal (if degenerate) route — RouteCursor
        // handles the empty-route case, so RouteModel must not reject it.
        var m = Load(EmptyRoute);

        Assert.Equal(RouteModel.CurrentVersion, m.Version);
        Assert.Empty(m.Steps);
    }

    [Fact]
    public void FromJson_carries_companion_json_verbatim_without_parsing()
    {
        const string arbitraryPayload = "{\"anything\": [1, 2, 3], \"nested\": {\"k\": \"v\"}}";
        var m = RouteModel.FromJson(
            GoodRoute,
            arbitraryPayload,       // overrides
            arbitraryPayload,       // area-objectives
            arbitraryPayload,       // area-transitions
            arbitraryPayload,       // area-targets
            arbitraryPayload);      // xp_curve

        Assert.Equal(arbitraryPayload, m.OverridesJson);
        Assert.Equal(arbitraryPayload, m.AreaObjectivesJson);
        Assert.Equal(arbitraryPayload, m.AreaTransitionsJson);
        Assert.Equal(arbitraryPayload, m.AreaTargetsJson);
        Assert.Equal(arbitraryPayload, m.XpCurveJson);
    }

    [Fact]
    public void FromJson_null_companion_strings_normalize_to_empty_string()
    {
        var m = RouteModel.FromJson(GoodRoute, null!, null!, null!, null!, null!);

        Assert.Equal(string.Empty, m.OverridesJson);
        Assert.Equal(string.Empty, m.AreaObjectivesJson);
        Assert.Equal(string.Empty, m.AreaTransitionsJson);
        Assert.Equal(string.Empty, m.AreaTargetsJson);
        Assert.Equal(string.Empty, m.XpCurveJson);
    }

    // ------------------------------------------------------------ graceful-failure contract

    [Fact]
    public void FromJson_wrong_schema_version_throws_typed_RouteSchemaMismatchException()
    {
        var ex = Assert.Throws<RouteSchemaMismatchException>(() => Load(WrongVersion));

        Assert.Contains("99", ex.Message);
        Assert.Contains(RouteModel.CurrentVersion.ToString(), ex.Message);
    }

    [Fact]
    public void FromJson_truncated_json_throws_RouteSchemaMismatchException_wrapping_JsonException()
    {
        // Graceful-degradation contract: consumers (WorldStateAdapter / RouteCursor construction site)
        // must be able to catch a single typed exception — not a bare System.Text.Json.JsonException.
        var ex = Assert.Throws<RouteSchemaMismatchException>(() => Load(Truncated));

        Assert.NotNull(ex.InnerException);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public void FromJson_json_null_literal_throws_typed_RouteSchemaMismatchException()
    {
        // JsonSerializer.Deserialize<T>("null") returns null cleanly; that has to funnel through the
        // typed exception too, not a NullReferenceException at the Version-check line.
        var ex = Assert.Throws<RouteSchemaMismatchException>(() => Load(JsonNull));

        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJson_null_routeJson_throws_ArgumentNullException()
    {
        // ArgumentNullException is the caller-contract violation; RouteSchemaMismatchException is
        // for content problems. Keep them distinct.
        Assert.Throws<ArgumentNullException>(() =>
            RouteModel.FromJson(null!, EmptyOverrides, EmptyAreaObj, EmptyAreaTrs, EmptyAreaTgt, EmptyXpCurve));
    }

    [Fact]
    public void FromJson_malformed_overrides_does_not_prevent_route_from_loading()
    {
        // Spec §5 EC2-DATA scope: "overrides.json malformed → route still loads with empty overrides."
        // Because RouteModel carries companion JSONs as raw strings (they're parsed lazily by their
        // downstream consumers), a broken overrides payload MUST NOT reject the whole route.
        const string BrokenOverrides = "{ not: valid json, missing quotes, [";

        var m = Load(GoodRoute, overrides: BrokenOverrides);

        Assert.Equal(2, m.Steps.Count);
        Assert.Equal(BrokenOverrides, m.OverridesJson);
    }

    [Fact]
    public void FromJson_malformed_all_companion_payloads_still_loads_route()
    {
        // Belt-and-braces: none of the five companions ever affects route parsing.
        const string Junk = "not-json-at-all";
        var m = RouteModel.FromJson(GoodRoute, Junk, Junk, Junk, Junk, Junk);

        Assert.Equal(2, m.Steps.Count);
        Assert.Equal(Junk, m.OverridesJson);
        Assert.Equal(Junk, m.AreaObjectivesJson);
        Assert.Equal(Junk, m.AreaTransitionsJson);
        Assert.Equal(Junk, m.AreaTargetsJson);
        Assert.Equal(Junk, m.XpCurveJson);
    }

    [Fact]
    public void RouteSchemaMismatchException_preserves_inner_exception_via_ctor()
    {
        // Direct ctor-level check so the typed-exception surface remains stable for consumers that
        // inspect the inner cause (e.g. crash-diag telemetry).
        var inner = new InvalidOperationException("root cause");
        var ex = new RouteSchemaMismatchException("wrapping message", inner);

        Assert.Equal("wrapping message", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Empty_static_singleton_is_current_version_with_no_steps()
    {
        Assert.Equal(RouteModel.CurrentVersion, RouteModel.Empty.Version);
        Assert.Empty(RouteModel.Empty.Steps);
    }
}
