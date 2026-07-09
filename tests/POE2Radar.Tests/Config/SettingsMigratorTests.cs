using System.IO;
using System.Text.Json;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// v0.20.1 T12: the 11 legacy one-shot bool fields (AtlasRulesInitialized,
/// AtlasTargetsSeeded, AtlasGroupsSeeded, AbyssRuleSeeded, IconDefaultsApplied,
/// IconDefaultsApplied2, RuleCleanupV1, MechanicLabelsV1, GroundDefaultsV2,
/// IconSizesV1, EntityArrowsSeeded) were consolidated into a single
/// <see cref="RadarSettings.AppliedMigrations"/> list. Backward-compat is
/// load-bearing: a v0.20.0 settings file with the legacy bools present must
/// migrate transparently on load.
/// </summary>
public class SettingsMigratorTests
{
    /// <summary>All 11 legacy bools set to true → all 11 migration keys land in AppliedMigrations.</summary>
    [Fact]
    public void Seeded_v020_json_migrates_to_AppliedMigrations()
    {
        var path = FixturePath("settings-v0.20.0-seeded.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);
        Assert.Contains("seed:atlas-rules",         settings.AppliedMigrations);
        Assert.Contains("seed:atlas-targets",       settings.AppliedMigrations);
        Assert.Contains("seed:atlas-groups",        settings.AppliedMigrations);
        Assert.Contains("seed:abyss-rule",          settings.AppliedMigrations);
        Assert.Contains("seed:icon-defaults-v1",    settings.AppliedMigrations);
        Assert.Contains("seed:icon-defaults-v2",    settings.AppliedMigrations);
        Assert.Contains("seed:rule-cleanup-v1",     settings.AppliedMigrations);
        Assert.Contains("seed:mechanic-labels-v1",  settings.AppliedMigrations);
        Assert.Contains("seed:ground-defaults-v2",  settings.AppliedMigrations);
        Assert.Contains("seed:icon-sizes-v1",       settings.AppliedMigrations);
        Assert.Contains("seed:entity-arrows",       settings.AppliedMigrations);
        // v0.22 campaign-probe adds a 12th entry ("probe_install_id_v1") when the seeded fixture
        // has no probeInstallId — the migrator mints one on first load.
        Assert.Contains("probe_install_id_v1",      settings.AppliedMigrations);
        Assert.Equal(12, settings.AppliedMigrations.Count);
    }

    /// <summary>All 11 legacy bools set to false → no legacy migration keys land, but the v0.22
    /// campaign-probe branch still mints a ProbeInstallId + stamps "probe_install_id_v1", so
    /// AppliedMigrations ends with exactly one entry.</summary>
    [Fact]
    public void Unseeded_v020_json_leaves_AppliedMigrations_with_only_probe_migration()
    {
        var path = FixturePath("settings-v0.20.0-unseeded.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);
        // Post-v0.22: unseeded v0.20 json still triggers the probe_install_id_v1 auto-populate
        // (the fixture has no probeInstallId, so the migrator mints one).
        Assert.Single(settings.AppliedMigrations);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }

    /// <summary>Modern JSON (no legacy bools, AppliedMigrations already populated) round-trips through
    /// the migrator without loss — post-v0.20.1 saves already omit legacy fields, and the migrator must
    /// not re-add anything from thin air. Post-v0.22 the fixture must also pin a probeInstallId so the
    /// probe branch no-ops and this test isolates legacy-passthrough behavior.</summary>
    [Fact]
    public void Modern_json_without_legacy_bools_passes_through()
    {
        var modern = "{\"AllowLanAccess\":true," +
                     "\"probeInstallId\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\"," +
                     "\"AppliedMigrations\":[\"seed:atlas-rules\"]}";
        using var doc = JsonDocument.Parse(modern);
        var settings = SettingsMigrator.Migrate(doc);
        Assert.True(settings.AllowLanAccess);
        Assert.Single(settings.AppliedMigrations);
        Assert.Contains("seed:atlas-rules", settings.AppliedMigrations);
    }

    static string FixturePath(string name)
        => Path.Combine(Path.GetDirectoryName(typeof(SettingsMigratorTests).Assembly.Location)!,
                        "fixtures", name);
}
