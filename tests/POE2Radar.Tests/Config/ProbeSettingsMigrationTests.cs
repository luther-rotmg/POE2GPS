using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// v0.22 campaign-probe: SettingsMigrator must auto-populate a random v4 UUID into
/// RadarSettings.ProbeInstallId on first load (empty/missing) and stamp
/// "probe_install_id_v1" into AppliedMigrations. Idempotent: a second Migrate call
/// on an already-populated json leaves ProbeInstallId unchanged and does not double-
/// append the key. Also verifies EnableCampaignProbe defaults to true and
/// ProbeOnboardingSeen defaults to false when absent from the input json.
/// </summary>
public class ProbeSettingsMigrationTests
{
    static readonly Regex UuidV4 =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase);

    [Fact]
    public void Empty_json_populates_ProbeInstallId_with_v4_uuid_and_stamps_migration()
    {
        using var doc = JsonDocument.Parse("{}");
        var settings = SettingsMigrator.Migrate(doc);

        Assert.False(string.IsNullOrEmpty(settings.ProbeInstallId));
        Assert.Matches(UuidV4, settings.ProbeInstallId);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }

    [Fact]
    public void Missing_probe_fields_get_defaults_EnableTrue_OnboardingFalse()
    {
        using var doc = JsonDocument.Parse("{}");
        var settings = SettingsMigrator.Migrate(doc);

        Assert.True(settings.EnableCampaignProbe);
        Assert.False(settings.ProbeOnboardingSeen);
    }

    [Fact]
    public void Preexisting_ProbeInstallId_is_preserved_and_migration_key_still_stamped_once()
    {
        var json = "{\"probeInstallId\":\"11111111-2222-4333-8444-555555555555\"," +
                   "\"appliedMigrations\":[\"probe_install_id_v1\"]}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.Equal("11111111-2222-4333-8444-555555555555", settings.ProbeInstallId);
        Assert.Single(settings.AppliedMigrations, "probe_install_id_v1");
    }

    [Fact]
    public void Empty_ProbeInstallId_string_is_treated_as_missing_and_populated()
    {
        var json = "{\"probeInstallId\":\"\"}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.Matches(UuidV4, settings.ProbeInstallId);
        Assert.Contains("probe_install_id_v1", settings.AppliedMigrations);
    }

    [Fact]
    public void EnableCampaignProbe_false_in_json_is_preserved()
    {
        var json = "{\"enableCampaignProbe\":false}";
        using var doc = JsonDocument.Parse(json);
        var settings = SettingsMigrator.Migrate(doc);

        Assert.False(settings.EnableCampaignProbe);
    }

    [Fact]
    public void ResetTraceSession_regenerates_a_new_v4_uuid()
    {
        var s = new RadarSettings { ProbeInstallId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee" };
        var before = s.ProbeInstallId;

        s.ResetTraceSession();

        Assert.NotEqual(before, s.ProbeInstallId);
        Assert.Matches(UuidV4, s.ProbeInstallId);
    }

    [Fact]
    public void ResetTraceSession_does_not_clear_ProbeOnboardingSeen()
    {
        // Regenerating the trace session id is orthogonal to the onboarding toast — the user has
        // already seen it, resetting the uuid must not re-fire that toast on next launch.
        var s = new RadarSettings
        {
            ProbeInstallId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            ProbeOnboardingSeen = true,
        };

        s.ResetTraceSession();

        Assert.True(s.ProbeOnboardingSeen);
    }

    [Fact]
    public void RadarSettings_default_ctor_matches_spec_defaults()
    {
        // Spec-locked defaults: EnableCampaignProbe=true (default ON), ProbeInstallId=""
        // (migrator populates), ProbeOnboardingSeen=false (toast fires once).
        var s = new RadarSettings();

        Assert.True(s.EnableCampaignProbe);
        Assert.Equal(string.Empty, s.ProbeInstallId);
        Assert.False(s.ProbeOnboardingSeen);
    }

    [Fact]
    public void RadarSettings_Load_persists_auto_populated_ProbeInstallId_across_reads()
    {
        // Simulate the shipped two-stage Load pattern: write a minimal json without probeInstallId,
        // run the migrator, verify a fresh uuid landed. Persist the migrator's output, re-parse,
        // re-migrate, and confirm the SAME uuid survives — proving the migration is idempotent
        // once the uuid + key are both persisted.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                         "poe2gps-probe-settings-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var file = System.IO.Path.Combine(dir, "radar_settings.json");
            System.IO.File.WriteAllText(file, "{\"allowLanAccess\":false}");

            string firstId;
            using (var doc1 = JsonDocument.Parse(System.IO.File.ReadAllText(file)))
            {
                var s1 = SettingsMigrator.Migrate(doc1);
                Assert.False(string.IsNullOrEmpty(s1.ProbeInstallId));
                firstId = s1.ProbeInstallId;

                var serialized = JsonSerializer.Serialize(s1,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    });
                System.IO.File.WriteAllText(file, serialized);
            }

            using (var doc2 = JsonDocument.Parse(System.IO.File.ReadAllText(file)))
            {
                var s2 = SettingsMigrator.Migrate(doc2);
                Assert.Equal(firstId, s2.ProbeInstallId);
                // Second migrate must NOT double-append the key.
                Assert.Single(s2.AppliedMigrations.FindAll(k => k == "probe_install_id_v1"));
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
