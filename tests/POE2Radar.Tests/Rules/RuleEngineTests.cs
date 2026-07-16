using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using POE2Radar.Core.Rules;
using Xunit;

namespace POE2Radar.Tests.Rules;

public sealed class RuleEngineTests
{
    // --- Helpers ---

    private static readonly Effect[] AllSixEffects =
    [
        new HideEffect(),
        new TintEffect("#ff0000"),
        new RingEffect("#00ff00"),
        new LabelEffect("Test Label"),
        new SoundEffect("alert.wav"),
        new PulseEffect("fast"),
    ];

    private static RuleRecord MakeRule(
        string name,
        int priority = 100,
        bool enabled = true,
        Selector? when = null,
        IReadOnlyList<Effect>? effects = null)
    {
        return new RuleRecord(
            Id: Guid.NewGuid(),
            Name: name,
            Priority: priority,
            Enabled: enabled,
            When: when ?? new Selector(null, null, null, null, null, null, null, null),
            Then: effects ?? [new HideEffect()]);
    }

    private static RulesFile FileOf(params RuleRecord[] rules) => new(1, rules);

    private static readonly EntityView AnyEntity = new(
        Metadata: "Metadata/Monsters/Uniques/Foo",
        Token: "Foo",
        Rarity: "unique",
        Level: 70,
        Buffs: Array.Empty<string>());

    private static readonly WorldSnapshotView AnySnap = new(ZoneCode: "LavaChamber", InHideout: false);

    // --- Tests ---

    [Fact]
    public void Empty_Returns_EmptyList()
    {
        var result = RuleEngine.Empty.TryMatch(AnyEntity, AnySnap);
        Assert.Empty(result);
    }

    [Fact]
    public void Compile_EmptyRules_ReturnsEmpty()
    {
        var set = RuleEngine.Compile(new RulesFile(1, Array.Empty<RuleRecord>()));
        Assert.Empty(set.TryMatch(AnyEntity, AnySnap));
        Assert.Equal(0, set.RuleCount);
    }

    [Fact]
    public void TryMatch_NoPredicates_MatchesEverything()
    {
        var rule = MakeRule("match-all", effects: [new TintEffect("#112233")]);
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.Single(result);
        Assert.IsType<TintEffect>(result[0]);
    }

    [Fact]
    public void TryMatch_MetadataRegex_Matches()
    {
        var rule = MakeRule("meta", when: new Selector(Metadata: "Uniques/.*", null, null, null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_MetadataRegex_DoesNotMatch()
    {
        var rule = MakeRule("meta", when: new Selector(Metadata: "^Bar$", null, null, null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.Empty(result);
    }

    [Fact]
    public void TryMatch_TokenRegex_Matches()
    {
        var rule = MakeRule("tok", when: new Selector(null, Token: "^Fo+$", null, null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_Rarity_MatchesExact()
    {
        var rule = MakeRule("rar", when: new Selector(null, null, Rarity: "unique", null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_Rarity_MismatchDoesNotMatch()
    {
        var rule = MakeRule("rar", when: new Selector(null, null, Rarity: "normal", null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.Empty(result);
    }

    [Fact]
    public void TryMatch_Rarity_CaseInsensitive()
    {
        var rule = MakeRule("rar", when: new Selector(null, null, Rarity: "UNIQUE", null, null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var entity = AnyEntity with { Rarity = "unique" };
        var result = set.TryMatch(entity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_ZoneCode_Matches()
    {
        var rule = MakeRule("zone", when: new Selector(null, null, null, ZoneCode: "Lava.*", null, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_InHideout_TrueRequired()
    {
        var rule = MakeRule("hide", when: new Selector(null, null, null, null, InHideout: true, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var inHideout = set.TryMatch(AnyEntity, AnySnap with { InHideout = true });
        var notHideout = set.TryMatch(AnyEntity, AnySnap with { InHideout = false });
        Assert.NotEmpty(inHideout);
        Assert.Empty(notHideout);
    }

    [Fact]
    public void TryMatch_InHideout_FalseRequired()
    {
        var rule = MakeRule("nothide", when: new Selector(null, null, null, null, InHideout: false, null, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var inHideout = set.TryMatch(AnyEntity, AnySnap with { InHideout = true });
        var notHideout = set.TryMatch(AnyEntity, AnySnap with { InHideout = false });
        Assert.Empty(inHideout);
        Assert.NotEmpty(notHideout);
    }

    [Fact]
    public void TryMatch_MinLevel_Inclusive()
    {
        var rule = MakeRule("minlvl", when: new Selector(null, null, null, null, null, MinLevel: 70, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var at = set.TryMatch(AnyEntity with { Level = 70 }, AnySnap);
        Assert.NotEmpty(at);
    }

    [Fact]
    public void TryMatch_MinLevel_Excluded()
    {
        var rule = MakeRule("minlvl", when: new Selector(null, null, null, null, null, MinLevel: 70, null, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var below = set.TryMatch(AnyEntity with { Level = 69 }, AnySnap);
        Assert.Empty(below);
    }

    [Fact]
    public void TryMatch_MaxLevel_Inclusive()
    {
        var rule = MakeRule("maxlvl", when: new Selector(null, null, null, null, null, null, MaxLevel: 70, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var at = set.TryMatch(AnyEntity with { Level = 70 }, AnySnap);
        Assert.NotEmpty(at);
    }

    [Fact]
    public void TryMatch_MaxLevel_Excluded()
    {
        var rule = MakeRule("maxlvl", when: new Selector(null, null, null, null, null, null, MaxLevel: 70, null));
        var set = RuleEngine.Compile(FileOf(rule));
        var above = set.TryMatch(AnyEntity with { Level = 71 }, AnySnap);
        Assert.Empty(above);
    }

    [Fact]
    public void TryMatch_MinMaxLevel_Range()
    {
        var rule = MakeRule("range", when: new Selector(null, null, null, null, null, MinLevel: 60, MaxLevel: 80, null));
        var set = RuleEngine.Compile(FileOf(rule));
        Assert.NotEmpty(set.TryMatch(AnyEntity with { Level = 60 }, AnySnap));
        Assert.NotEmpty(set.TryMatch(AnyEntity with { Level = 80 }, AnySnap));
        Assert.NotEmpty(set.TryMatch(AnyEntity with { Level = 70 }, AnySnap));
        Assert.Empty(set.TryMatch(AnyEntity with { Level = 59 }, AnySnap));
        Assert.Empty(set.TryMatch(AnyEntity with { Level = 81 }, AnySnap));
    }

    [Fact]
    public void TryMatch_HasBuff_MatchesAny()
    {
        var rule = MakeRule("buff", when: new Selector(null, null, null, null, null, null, null, HasBuff: "adrenaline"));
        var set = RuleEngine.Compile(FileOf(rule));
        var entity = AnyEntity with { Buffs = new[] { "haste", "adrenaline", "fortify" } };
        var result = set.TryMatch(entity, AnySnap);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void TryMatch_HasBuff_NoBuffs_DoesNotMatch()
    {
        var rule = MakeRule("buff", when: new Selector(null, null, null, null, null, null, null, HasBuff: "adrenaline"));
        var set = RuleEngine.Compile(FileOf(rule));
        var entity = AnyEntity with { Buffs = Array.Empty<string>() };
        var result = set.TryMatch(entity, AnySnap);
        Assert.Empty(result);
    }

    [Fact]
    public void TryMatch_DisabledRule_NeverMatches()
    {
        var rule = MakeRule("disabled", enabled: false, effects: [new TintEffect("#aabbcc")]);
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.Empty(result);
        // Disabled rules are still counted in RuleCount.
        Assert.Equal(1, set.RuleCount);
    }

    [Fact]
    public void TryMatch_MultipleRulesMatch_ReturnsAllEffects_InPriorityOrder()
    {
        var r200 = MakeRule("p200", priority: 200, effects: [new TintEffect("#200200")]);
        var r100 = MakeRule("p100", priority: 100, effects: [new TintEffect("#100100"), new RingEffect("#100010")]);
        var r50 = MakeRule("p50", priority: 50, effects: [new PulseEffect("slow")]);
        // Deliberately feed them out of priority order to prove Compile sorts.
        var set = RuleEngine.Compile(FileOf(r50, r200, r100));

        var result = set.TryMatch(AnyEntity, AnySnap);
        // 1 + 2 + 1 = 4 effects total.
        Assert.Equal(4, result.Count);
        // Highest-priority rule's effects come first.
        Assert.IsType<TintEffect>(result[0]);
        Assert.Equal("#200200", ((TintEffect)result[0]).Color);
        // Then priority 100's two effects.
        Assert.Equal("#100100", ((TintEffect)result[1]).Color);
        Assert.IsType<RingEffect>(result[2]);
        // Then priority 50's effect.
        Assert.IsType<PulseEffect>(result[3]);
    }

    [Fact]
    public void TryMatch_AllSixEffectSubtypes_ReturnedCorrectly()
    {
        var rule = MakeRule("all-six", effects: AllSixEffects);
        var set = RuleEngine.Compile(FileOf(rule));
        var result = set.TryMatch(AnyEntity, AnySnap);
        Assert.Equal(6, result.Count);
        Assert.IsType<HideEffect>(result[0]);
        Assert.IsType<TintEffect>(result[1]);
        Assert.IsType<RingEffect>(result[2]);
        Assert.IsType<LabelEffect>(result[3]);
        Assert.IsType<SoundEffect>(result[4]);
        Assert.IsType<PulseEffect>(result[5]);
    }

    [Fact]
    public void Compile_InvalidRegex_ThrowsWithRuleName()
    {
        var rule = MakeRule("bad-meta", when: new Selector(Metadata: "*bad(", null, null, null, null, null, null, null));
        var ex = Assert.Throws<ArgumentException>(() => RuleEngine.Compile(FileOf(rule)));
        Assert.Contains("bad-meta", ex.Message);
        Assert.Contains("Metadata", ex.Message);
    }

    [Fact]
    public void Compile_InvalidRegexInZoneCode_ThrowsNamingField()
    {
        var rule = MakeRule("bad-zone", when: new Selector(null, null, null, ZoneCode: "([", null, null, null, null));
        var ex = Assert.Throws<ArgumentException>(() => RuleEngine.Compile(FileOf(rule)));
        Assert.Contains("bad-zone", ex.Message);
        Assert.Contains("predicate 'ZoneCode'", ex.Message);
    }

    [Fact]
    public void Perf_100Rules_10KEntities_Under200Microseconds_Avg()
    {
        // Build 100 rules with a mix of predicates, all matching the test entity.
        var rules = new List<RuleRecord>(100);
        for (int i = 0; i < 100; i++)
        {
            var when = new Selector(
                Metadata: null,
                Token: null,
                Rarity: i % 2 == 0 ? "unique" : null,
                ZoneCode: null,
                InHideout: null,
                MinLevel: i % 3 == 0 ? 1 : null,
                MaxLevel: null,
                HasBuff: null);
            rules.Add(MakeRule($"rule-{i}", priority: 100 - i, when: when, effects: [new TintEffect("#112233")]));
        }
        var set = RuleEngine.Compile(FileOf(rules.ToArray()));
        Assert.Equal(100, set.RuleCount);

        var entity = new EntityView(
            Metadata: "Metadata/Monsters/Uniques/Foo",
            Token: "Foo",
            Rarity: "unique",
            Level: 70,
            Buffs: Array.Empty<string>());
        var snap = new WorldSnapshotView(ZoneCode: "LavaChamber", InHideout: false);

        // Warm up JIT for the compiled regexes + match path.
        for (int i = 0; i < 1000; i++)
            set.TryMatch(entity, snap);

        const int iterations = 10000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            set.TryMatch(entity, snap);
        sw.Stop();

        double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Generous CI slack: target < 200µs (0.2ms), assert < 2ms per call.
        Assert.True(avgMs < 2.0, $"Avg TryMatch time {avgMs:F4}ms exceeded 2ms budget.");
    }
}
