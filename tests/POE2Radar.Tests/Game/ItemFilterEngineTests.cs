using POE2Radar.Core.Game;
using Xunit;
using RawAffix = POE2Radar.Core.Game.Poe2Live.RawAffix;

namespace POE2Radar.Tests.Game;

public class ItemFilterEngineTests
{
    private static ItemFilterEngine EmptyEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var e = new ItemFilterEngine(path);
        e.Replace(Array.Empty<FilterRule>());
        return e;
    }

    private static RawAffix Aff(string modId, params int[] values) => new(modId, values);

    [Fact]
    public void Match_empty_engine_returns_empty()
    {
        var e = EmptyEngine();
        var matches = e.Match(Array.Empty<RawAffix>(), Array.Empty<RawAffix>());
        Assert.Empty(matches);
    }

    [Fact]
    public void Match_disabled_filter_never_matches()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", false, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 0, null, null, null),
        }));
        var matches = e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 20) });
        Assert.Empty(matches);
    }

    [Fact]
    public void Match_empty_requirement_list_never_matches()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, Array.Empty<FilterRequirement>()));
        var matches = e.Match(Array.Empty<RawAffix>(), new[] { Aff("Anything", 20) });
        Assert.Empty(matches);
    }

    [Fact]
    public void Match_presence_only_op_gte_zero_matches_when_stat_present()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 0, null, null, null),
        }));
        var matches = e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 12) });
        Assert.Single(matches);
    }

    [Fact]
    public void Match_threshold_boundary_inclusive()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 10) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 9) }));
    }

    [Fact]
    public void Match_AND_requires_all_requirements_to_pass()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
            new FilterRequirement("FasterRecharge", ">=", 5, null, null, null),
        }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15), Aff("FasterRecharge_1", 8) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) }));
    }

    [Fact]
    public void Match_priority_sorts_matches_desc()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("low", "Low", true, "#00FF00", 50, new[] { new FilterRequirement("EnergyShield", ">=", 0, null, null, null) }));
        e.Add(new FilterRule("hi", "Hi", true, "#FF0000", 100, new[] { new FilterRequirement("EnergyShield", ">=", 0, null, null, null) }));
        var matches = e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) });
        Assert.Equal(2, matches.Count);
        Assert.Equal("hi", matches[0].Rule.Id);
        Assert.Equal("low", matches[1].Rule.Id);
    }

    [Fact]
    public void Match_op_less_than_or_equal()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("EnergyShield", "<=", 20, null, null, null) }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 25) }));
    }

    [Fact]
    public void Match_op_equal()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("EnergyShield", "==", 15, null, null, null) }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 14) }));
    }

    [Fact]
    public void Match_op_between_inclusive_boundaries()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("EnergyShield", "between", 10, 20, null, null) }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 10) }));
        Assert.Single(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 20) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 9) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 21) }));
    }

    [Fact]
    public void Match_multiple_affixes_any_passing_satisfies()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("EnergyShield", ">=", 15, null, null, null) }));
        var matches = e.Match(Array.Empty<RawAffix>(), new[]
        {
            Aff("EnergyShield_1", 10),   // fails
            Aff("EnergyShield_2", 18),   // passes → requirement satisfied
        });
        Assert.Single(matches);
    }

    [Fact]
    public void Match_stat_not_present_no_match()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("Life", ">=", 60, null, null, null) }));
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 15) }));
    }

    [Fact]
    public void Match_scope_implicit_only_rejects_explicit_affixes()
    {
        var e = EmptyEngine();
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 0, null, new[] { "implicit" }, null)
        }));
        // Affix only in explicit list → scope implicit-only should reject.
        Assert.Empty(e.Match(Array.Empty<RawAffix>(), new[] { Aff("EnergyShield_1", 12) }));
        // Same affix in implicit list → passes.
        Assert.Single(e.Match(new[] { Aff("EnergyShield_1", 12) }, Array.Empty<RawAffix>()));
    }

    [Fact]
    public void Generation_bumps_on_mutation()
    {
        var e = EmptyEngine();
        var g0 = e.Generation;
        e.Add(new FilterRule("t1", "T1", true, "#FF0000", 100, new[] { new FilterRequirement("X", ">=", 0, null, null, null) }));
        Assert.True(e.Generation > g0);
        var g1 = e.Generation;
        e.RemoveAt(0);
        Assert.True(e.Generation > g1);
    }
}
