using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// v0.42.3 migration tests for <see cref="RadarSettings.Migrate"/>. The one-time flag
/// <see cref="RadarSettings.AutoAdaptTickCadenceMigratedV0423"/> gates the forced-false
/// override of <see cref="RadarSettings.AutoAdaptTickCadence"/> for users upgrading from
/// v0.42.1 (which shipped the field default as true and persisted that to disk, making
/// the v0.42.2 default-flip inert on load).
/// </summary>
public sealed class RadarSettingsMigrateTests
{
    [Fact]
    public void v0423_Migrate_UnmigratedWithAdaptTrue_ForcesFalseAndSetsFlag()
    {
        // The exact scenario that motivated this migration: a v0.42.1 user's persisted
        // settings deserialize with AutoAdaptTickCadence=true (from v0.42.1's default,
        // which System.Text.Json wrote to disk with DefaultIgnoreCondition.Never), and
        // the migration flag is still false because it's a brand-new v0.42.3 field.
        var s = new RadarSettings
        {
            AutoAdaptTickCadence = true,
            AutoAdaptTickCadenceMigratedV0423 = false,
        };

        var changed = s.Migrate();

        Assert.True(changed, "Migrate should return true when the migration actually flipped state");
        Assert.False(s.AutoAdaptTickCadence, "Migration should force AutoAdaptTickCadence to false");
        Assert.True(s.AutoAdaptTickCadenceMigratedV0423, "Migration flag should be set to true");
    }

    [Fact]
    public void v0423_Migrate_AlreadyMigrated_PreservesUserExplicitOptIn()
    {
        // Post-migration, a user who explicitly re-enables AutoAdaptTickCadence in their
        // config file should have their choice respected. Migrate() runs on every Load()
        // and must be idempotent — the flag being true means "we've already forced-false
        // once; don't touch it again."
        var s = new RadarSettings
        {
            AutoAdaptTickCadence = true,
            AutoAdaptTickCadenceMigratedV0423 = true,   // user has already been through the migration once
        };

        var changed = s.Migrate();

        Assert.False(changed, "Migrate should NOT report a change when the flag is already set");
        Assert.True(s.AutoAdaptTickCadence, "User's post-migration explicit opt-in must be preserved");
        Assert.True(s.AutoAdaptTickCadenceMigratedV0423);
    }

    [Fact]
    public void v0423_Migrate_UnmigratedWithAdaptFalse_JustSetsFlag()
    {
        // A fresh v0.42.2 install already had AutoAdaptTickCadence=false. The migration
        // still runs to set the flag, so the "have I done this migration yet" state is
        // recorded — but it doesn't need to touch AutoAdaptTickCadence itself.
        var s = new RadarSettings
        {
            AutoAdaptTickCadence = false,
            AutoAdaptTickCadenceMigratedV0423 = false,
        };

        var changed = s.Migrate();

        // changed is true because the flag itself is a state change (triggers Save()).
        Assert.True(changed);
        Assert.False(s.AutoAdaptTickCadence);
        Assert.True(s.AutoAdaptTickCadenceMigratedV0423);
    }
}
