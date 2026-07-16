using System;
using System.Collections.Generic;
using System.Linq;
using POE2Radar.Core.Game;
using POE2Radar.Core.Rules;
using Xunit;

namespace POE2Radar.Tests.Game;

public sealed class ItemFilterEngineRuleFilterTests
{
    // ── Helpers ──

    private static readonly EntityView AnyEntity = new(
        Metadata: "Metadata/Items/Weapons/Sword",
        Token: "Sword",
        Rarity: "rare",
        Level: 70,
        Buffs: Array.Empty<string>());

    private static readonly WorldSnapshotView AnySnap = new(ZoneCode: "LavaChamber", InHideout: false);

    private static ItemFilterEngine CreateEngine(CompiledRuleSet rules)
    {
        var engine = new ItemFilterEngine(Path.GetTempFileName());
        engine.Rules = rules;
        return engine;
    }

    private static IReadOnlyList<ItemFilterEngine.MatchedFilter> SomeResults()
    {
        return new List<ItemFilterEngine.MatchedFilter>
        {
            new(new FilterRule("r1", "Rule1", true, "#FF0000", 100, Array.Empty<FilterRequirement>()), "matched"),
        };
    }

    private static RuleRecord MakeHideRule(string name = "hide-test", Selector? when = null)
    {
        return new RuleRecord(
            Guid.NewGuid(),
            name,
            100,
            true,
            when ?? new Selector(Metadata: ".*", null, null, null, null, null, null, null),
            new[] { new HideEffect() });
    }

    // ── Tests ──

    [Fact]
    public void ApplyRuleFilter_EmptyRules_ReturnsResultsUnchanged()
    {
        var engine = CreateEngine(RuleEngine.Empty);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Equal(results, filtered);
    }

    [Fact]
    public void ApplyRuleFilter_MatchingHideRule_ReturnsEmptyList()
    {
        var rule = MakeHideRule();
        var rules = RuleEngine.Compile(new RulesFile(1, new[] { rule }));
        var engine = CreateEngine(rules);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Empty(filtered);
    }

    [Fact]
    public void ApplyRuleFilter_NonMatchingRule_ReturnsResultsUnchanged()
    {
        var rule = MakeHideRule(when: new Selector(Metadata: "^NeverMatch$", null, null, null, null, null, null, null));
        var rules = RuleEngine.Compile(new RulesFile(1, new[] { rule }));
        var engine = CreateEngine(rules);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Equal(results, filtered);
    }

    [Fact]
    public void ApplyRuleFilter_TintEffect_NotHide_ReturnsResultsUnchanged()
    {
        var rule = new RuleRecord(
            Guid.NewGuid(),
            "tint-test",
            100,
            true,
            new Selector(Metadata: ".*", null, null, null, null, null, null, null),
            new[] { new TintEffect("#ff0000") });
        var rules = RuleEngine.Compile(new RulesFile(1, new[] { rule }));
        var engine = CreateEngine(rules);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Equal(results, filtered);
    }

    [Fact]
    public void ApplyRuleFilter_MultipleEffectsIncludingHide_FiltersOut()
    {
        var rule = new RuleRecord(
            Guid.NewGuid(),
            "multi-test",
            100,
            true,
            new Selector(Metadata: ".*", null, null, null, null, null, null, null),
            new Effect[] { new TintEffect("#ff0000"), new HideEffect() });
        var rules = RuleEngine.Compile(new RulesFile(1, new[] { rule }));
        var engine = CreateEngine(rules);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Empty(filtered);
    }

    [Fact]
    public void ApplyRuleFilter_DisabledRule_DoesNotFilter()
    {
        var rule = new RuleRecord(
            Guid.NewGuid(),
            "disabled-hide",
            100,
            false,
            new Selector(Metadata: ".*", null, null, null, null, null, null, null),
            new[] { new HideEffect() });
        var rules = RuleEngine.Compile(new RulesFile(1, new[] { rule }));
        var engine = CreateEngine(rules);
        var results = SomeResults();
        var filtered = engine.ApplyRuleFilter(results, AnyEntity, AnySnap);
        Assert.Equal(results, filtered);
    }
}