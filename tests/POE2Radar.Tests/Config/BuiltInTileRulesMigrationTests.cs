using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// Threshold — the WaygateDevice built-in Tile display rule is seeded exactly once via
/// <see cref="DisplayRules.SeedBuiltInTileRulesIfNeeded"/>, guarded by the
/// <c>built_in_tile_rules_v1</c> entry in <see cref="RadarSettings.AppliedMigrations"/>.
/// A second call on the same settings instance must NOT double-stamp the key, and a
/// hand-edited legacy <c>builtInTileRulesSeeded:true</c> json field must fold into the
/// same key so a future "was this ever run" audit sees one source of truth.
/// </summary>
public class BuiltInTileRulesMigrationTests
{
    /// <summary>Idempotency lock: the first call appends the migration key; the second call
    /// short-circuits at the Contains(...) guard and leaves AppliedMigrations unchanged (no
    /// duplicate key, no re-added rule downstream). This is the "second call no-ops" contract.</summary>
    [Fact]
    public void SeedBuiltInTileRulesIfNeeded_SecondCall_NoOps()
    {
        var settings = new RadarSettings();
        var beforeCount = settings.AppliedMigrations.Count;

        DisplayRules.SeedBuiltInTileRulesIfNeeded(settings);
        Assert.Contains(DisplayRules.BuiltInTileRulesMigrationKey, settings.AppliedMigrations);
        var afterFirst = settings.AppliedMigrations.Count;
        Assert.Equal(beforeCount + 1, afterFirst);

        DisplayRules.SeedBuiltInTileRulesIfNeeded(settings);
        Assert.Equal(afterFirst, settings.AppliedMigrations.Count);
        Assert.Single(settings.AppliedMigrations,
            k => k == DisplayRules.BuiltInTileRulesMigrationKey);
    }

    /// <summary>Null-guard on <c>RadarSettings</c>: a null settings arg is a NO-OP (no throw).
    /// Defence in depth for the RadarApp wire in the case of a settings-load failure path.</summary>
    [Fact]
    public void SeedBuiltInTileRulesIfNeeded_NullSettings_DoesNotThrow()
    {
        // Should silently return without touching anything or throwing.
        var ex = Record.Exception(() => DisplayRules.SeedBuiltInTileRulesIfNeeded(null!));
        Assert.Null(ex);
    }

    /// <summary>Null-guard on <c>AppliedMigrations</c>: if the settings arrived with a null
    /// list (partial deserialize of a hand-edited json), the seed must initialize it before
    /// stamping. Verifies the <c>settings.AppliedMigrations ??= new()</c> line is load-bearing.</summary>
    [Fact]
    public void SeedBuiltInTileRulesIfNeeded_NullAppliedMigrations_InitializesAndStamps()
    {
        var settings = new RadarSettings { AppliedMigrations = null! };

        DisplayRules.SeedBuiltInTileRulesIfNeeded(settings);

        Assert.NotNull(settings.AppliedMigrations);
        Assert.Contains(DisplayRules.BuiltInTileRulesMigrationKey, settings.AppliedMigrations);
    }

    /// <summary>The seed set returned by <see cref="DisplayRules.BuiltInTileRules"/> matches
    /// the exact Threshold spec: one rule, name Waygate, Categories=[Tile], Match=[WaygateDevice],
    /// Shape=Eye, Color=#00E5FF, Navigable=true. Pins the shape a future R5 atlas-landmark port
    /// must not silently drift from.</summary>
    [Fact]
    public void BuiltInTileRules_Returns_Single_WaygateRule_With_Spec_Shape()
    {
        var seed = DisplayRules.BuiltInTileRules();

        Assert.Single(seed);
        var rule = seed[0];
        Assert.True(rule.Enabled);
        Assert.Equal("Waygate", rule.Name);
        Assert.Equal(new List<string> { "Tile" }, rule.Categories);
        Assert.Equal(new List<string> { "WaygateDevice" }, rule.Match);
        Assert.Equal("Eye", rule.Shape);
        Assert.Equal("#00E5FF", rule.Color);
        Assert.True(rule.Navigable);
    }

    /// <summary>Defensive path: the fork never shipped a legacy <c>BuiltInTileRulesSeeded</c>
    /// bool, but the v0.20.1 AppliedMigrations consolidation pattern requires a Map entry so a
    /// hand-edited json (or a future upstream sync that surfaces the legacy field) folds into
    /// the same <c>built_in_tile_rules_v1</c> key. Feeds a minimal json with the legacy bool
    /// set to true and asserts the migration key lands in the settings.</summary>
    [Fact]
    public void SettingsMigrator_Map_Has_BuiltInTileRulesSeeded_Legacy_Entry()
    {
        var json = "{\"builtInTileRulesSeeded\":true," +
                   "\"probeInstallId\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"}";
        using var doc = JsonDocument.Parse(json);

        var settings = SettingsMigrator.Migrate(doc);

        Assert.Contains("built_in_tile_rules_v1", settings.AppliedMigrations);
    }
}
