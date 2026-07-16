using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using POE2Radar.Core.Rules;
using Xunit;

namespace POE2Radar.Tests.Rules;

public sealed class RulesFileStoreTests : IDisposable
{
    private readonly string _configDir;

    public RulesFileStoreTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "poe2gps-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // --- Helpers ---

    private static Effect[] AllSixEffects() =>
    [
        new HideEffect(),
        new TintEffect("#ff0000"),
        new RingEffect("#00ff00"),
        new LabelEffect("Test Label"),
        new SoundEffect("alert.wav"),
        new PulseEffect("fast"),
    ];

    private static RuleRecord MakeRule(Guid id, string name, int priority = 100, bool enabled = true, IReadOnlyList<Effect>? effects = null)
    {
        return new RuleRecord(
            Id: id,
            Name: name,
            Priority: priority,
            Enabled: enabled,
            When: new Selector(null, null, null, null, null, null, null, null),
            Then: effects ?? [new HideEffect()]);
    }

    // --- Tests ---

    [Fact]
    public void Load_MissingFile_ReturnsEmptyRulesFile()
    {
        var file = RulesFileStore.Load(_configDir);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Empty(file.Rules);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var rules = new List<RuleRecord>
        {
            MakeRule(id1, "Hide Uniques", 100, true, [new HideEffect()]),
            MakeRule(id2, "Tint Rares", 200, true, [new TintEffect("#ff0000"), new RingEffect("#00ff00")]),
            MakeRule(id3, "Pulse & Label", 50, false, [new PulseEffect("slow"), new LabelEffect("Watch out!")]),
        };

        var file = new RulesFile(1, rules.AsReadOnly());
        RulesFileStore.Save(_configDir, file);

        var loaded = RulesFileStore.Load(_configDir);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(3, loaded.Rules.Count);

        // Verify first rule
        Assert.Equal(id1, loaded.Rules[0].Id);
        Assert.Equal("Hide Uniques", loaded.Rules[0].Name);
        Assert.Equal(100, loaded.Rules[0].Priority);
        Assert.True(loaded.Rules[0].Enabled);
        Assert.Single(loaded.Rules[0].Then);
        Assert.IsType<HideEffect>(loaded.Rules[0].Then[0]);

        // Verify second rule
        Assert.Equal(id2, loaded.Rules[1].Id);
        Assert.Equal("Tint Rares", loaded.Rules[1].Name);
        Assert.Equal(200, loaded.Rules[1].Priority);
        Assert.True(loaded.Rules[1].Enabled);
        Assert.Equal(2, loaded.Rules[1].Then.Count);
        var tint2 = Assert.IsType<TintEffect>(loaded.Rules[1].Then[0]);
        Assert.Equal("#ff0000", tint2.Color);
        var ring2 = Assert.IsType<RingEffect>(loaded.Rules[1].Then[1]);
        Assert.Equal("#00ff00", ring2.Color);

        // Verify third rule
        Assert.Equal(id3, loaded.Rules[2].Id);
        Assert.Equal("Pulse & Label", loaded.Rules[2].Name);
        Assert.Equal(50, loaded.Rules[2].Priority);
        Assert.False(loaded.Rules[2].Enabled);
        Assert.Equal(2, loaded.Rules[2].Then.Count);
        var pulse3 = Assert.IsType<PulseEffect>(loaded.Rules[2].Then[0]);
        Assert.Equal("slow", pulse3.Speed);
        var label3 = Assert.IsType<LabelEffect>(loaded.Rules[2].Then[1]);
        Assert.Equal("Watch out!", label3.Text);
    }

    [Fact]
    public void Save_AssignsIdToEmptyGuid()
    {
        var rule = MakeRule(Guid.Empty, "No Id Yet");
        var file = new RulesFile(1, new[] { rule }.ToList().AsReadOnly());

        RulesFileStore.Save(_configDir, file);

        var loaded = RulesFileStore.Load(_configDir);
        var savedRule = Assert.Single(loaded.Rules);
        Assert.NotEqual(Guid.Empty, savedRule.Id);
    }

    [Fact]
    public void Upsert_NewRule_Appends()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id, "New Rule");

        var returned = RulesFileStore.Upsert(_configDir, rule);

        Assert.Equal(id, returned.Id);
        Assert.Equal("New Rule", returned.Name);

        var loaded = RulesFileStore.Load(_configDir);
        Assert.Single(loaded.Rules);
        Assert.Equal(id, loaded.Rules[0].Id);
    }

    [Fact]
    public void Upsert_ExistingId_Replaces()
    {
        var id = Guid.NewGuid();
        var original = MakeRule(id, "Original");
        RulesFileStore.Upsert(_configDir, original);

        var replacement = MakeRule(id, "Replaced");
        var returned = RulesFileStore.Upsert(_configDir, replacement);

        Assert.Equal(id, returned.Id);
        Assert.Equal("Replaced", returned.Name);

        var loaded = RulesFileStore.Load(_configDir);
        Assert.Single(loaded.Rules);
        Assert.Equal("Replaced", loaded.Rules[0].Name);
    }

    [Fact]
    public void Upsert_EmptyGuid_AssignsNewId()
    {
        var rule = MakeRule(Guid.Empty, "Auto Id");

        var returned = RulesFileStore.Upsert(_configDir, rule);

        Assert.NotEqual(Guid.Empty, returned.Id);
        Assert.Equal("Auto Id", returned.Name);

        var loaded = RulesFileStore.Load(_configDir);
        var saved = Assert.Single(loaded.Rules);
        Assert.Equal(returned.Id, saved.Id);
    }

    [Fact]
    public void Delete_ExistingId_RemovesAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id, "To Delete");
        RulesFileStore.Upsert(_configDir, rule);

        var result = RulesFileStore.Delete(_configDir, id);

        Assert.True(result);

        var loaded = RulesFileStore.Load(_configDir);
        Assert.Empty(loaded.Rules);
    }

    [Fact]
    public void Delete_MissingId_ReturnsFalseNoThrow()
    {
        // Should not throw even though file doesn't exist
        var result = RulesFileStore.Delete(_configDir, Guid.NewGuid());
        Assert.False(result);
    }

    [Fact]
    public void Save_Over100Rules_Throws()
    {
        var rules = new List<RuleRecord>();
        for (int i = 0; i < 101; i++)
            rules.Add(MakeRule(Guid.NewGuid(), $"Rule{i}"));

        var file = new RulesFile(1, rules.AsReadOnly());

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.Save(_configDir, file));
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void Upsert_Over100Rules_Throws()
    {
        // Fill with 100 rules
        var rules = new List<RuleRecord>();
        for (int i = 0; i < 100; i++)
            rules.Add(MakeRule(Guid.NewGuid(), $"Rule{i}"));

        RulesFileStore.Save(_configDir, new RulesFile(1, rules.AsReadOnly()));

        // Try to add one more
        var ex = Assert.Throws<ArgumentException>(() =>
            RulesFileStore.Upsert(_configDir, MakeRule(Guid.NewGuid(), "Overflow")));
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void ValidateRule_EmptyName_Throws()
    {
        var rule = MakeRule(Guid.NewGuid(), "");

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRule_NameOver80_Throws()
    {
        var rule = MakeRule(Guid.NewGuid(), new string('x', 81));

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("80", ex.Message);
    }

    [Fact]
    public void ValidateRule_InvalidHexColor_Throws()
    {
        // TintEffect with bad hex
        var rule1 = MakeRule(Guid.NewGuid(), "Bad Tint", effects: [new TintEffect("not-hex")]);
        var ex1 = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule1));
        Assert.Contains("color", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // RingEffect with bad hex
        var rule2 = MakeRule(Guid.NewGuid(), "Bad Ring", effects: [new RingEffect("#zzzzzz")]);
        var ex2 = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule2));
        Assert.Contains("color", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRule_UnknownRarity_Throws()
    {
        var rule = new RuleRecord(
            Guid.NewGuid(),
            "Bad Rarity",
            100,
            true,
            new Selector(null, null, "legendary", null, null, null, null, null),
            [new HideEffect()]);

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("rarity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRule_BadSoundFileName_Throws()
    {
        // Path traversal attempt
        var rule1 = MakeRule(Guid.NewGuid(), "Bad Sound 1", effects: [new SoundEffect("../evil.wav")]);
        var ex1 = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule1));
        Assert.Contains("sound", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // Space in filename
        var rule2 = MakeRule(Guid.NewGuid(), "Bad Sound 2", effects: [new SoundEffect("cool sound.wav")]);
        var ex2 = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule2));
        Assert.Contains("sound", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRule_LabelOver200Chars_Throws()
    {
        var longText = new string('x', 201);
        var rule = MakeRule(Guid.NewGuid(), "Long Label", effects: [new LabelEffect(longText)]);

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("200", ex.Message);
    }

    [Fact]
    public void ValidateRule_EmptyThenList_Throws()
    {
        var rule = MakeRule(Guid.NewGuid(), "Empty Then", effects: Array.Empty<Effect>());

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("then", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRule_UnknownPulseSpeed_Throws()
    {
        var rule = MakeRule(Guid.NewGuid(), "Bad Speed", effects: [new PulseEffect("medium")]);

        var ex = Assert.Throws<ArgumentException>(() => RulesFileStore.ValidateRule(rule));
        Assert.Contains("speed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTrip_PolymorphicEffects_AllSubtypesDeserializeCorrectly()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id, "All Effects", effects: AllSixEffects());
        var file = new RulesFile(1, new[] { rule }.ToList().AsReadOnly());

        RulesFileStore.Save(_configDir, file);

        var loaded = RulesFileStore.Load(_configDir);
        var savedRule = Assert.Single(loaded.Rules);
        Assert.Equal(6, savedRule.Then.Count);

        // Verify each effect is its correct concrete type
        Assert.IsType<HideEffect>(savedRule.Then[0]);
        Assert.IsType<TintEffect>(savedRule.Then[1]);
        Assert.IsType<RingEffect>(savedRule.Then[2]);
        Assert.IsType<LabelEffect>(savedRule.Then[3]);
        Assert.IsType<SoundEffect>(savedRule.Then[4]);
        Assert.IsType<PulseEffect>(savedRule.Then[5]);

        // Verify values survived
        Assert.Equal("#ff0000", ((TintEffect)savedRule.Then[1]).Color);
        Assert.Equal("#00ff00", ((RingEffect)savedRule.Then[2]).Color);
        Assert.Equal("Test Label", ((LabelEffect)savedRule.Then[3]).Text);
        Assert.Equal("alert.wav", ((SoundEffect)savedRule.Then[4]).File);
        Assert.Equal("fast", ((PulseEffect)savedRule.Then[5]).Speed);
    }
}