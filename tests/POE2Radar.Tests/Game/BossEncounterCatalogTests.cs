using System.Linq;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

/// <summary>
/// Reach — CHOR-42 (v0.26). Locks the loader shape (nonempty on shipped resource, tolerant of
/// malformed lookups) and the three lookup surfaces the UI + overlay will consume:
/// by boss key, by zone code, by entity-metadata substring.
/// </summary>
public class BossEncounterCatalogTests
{
    [Fact]
    public void Shared_loads_nonempty_catalog_from_embedded_json()
    {
        var cat = BossEncounterCatalog.Shared;
        Assert.True(cat.Count > 0, $"Expected shipped boss_encounters.json to load at least one entry (got {cat.Count}).");
    }

    [Fact]
    public void ByBossKey_returns_matching_entry_for_shipped_key()
    {
        var cat = BossEncounterCatalog.Shared;
        var arbiter = cat.ByBossKey("arbiter_of_ash");
        Assert.NotNull(arbiter);
        Assert.Equal("arbiter_of_ash", arbiter!.Key);
        Assert.False(string.IsNullOrEmpty(arbiter.Label));
        Assert.NotEmpty(arbiter.OneShots);
    }

    [Fact]
    public void ByBossKey_null_or_missing_returns_null()
    {
        var cat = BossEncounterCatalog.Shared;
        Assert.Null(cat.ByBossKey(""));
        Assert.Null(cat.ByBossKey("does_not_exist_xyzzy"));
        Assert.Null(cat.ByBossKey(null!));
    }

    [Fact]
    public void ByZoneCode_returns_entry_for_shipped_zone()
    {
        var cat = BossEncounterCatalog.Shared;
        // The shipped catalog includes MapUberBoss_CopperCitadel for The Arbiter of Ash.
        var e = cat.ByZoneCode("MapUberBoss_CopperCitadel");
        Assert.NotNull(e);
        Assert.Equal("arbiter_of_ash", e!.Key);
    }

    [Fact]
    public void ByZoneCode_case_insensitive()
    {
        var cat = BossEncounterCatalog.Shared;
        var e = cat.ByZoneCode("mapuberboss_coppercitadel");
        Assert.NotNull(e);
        Assert.Equal("arbiter_of_ash", e!.Key);
    }

    [Fact]
    public void ByMetadata_substring_match_first_wins()
    {
        var cat = BossEncounterCatalog.Shared;
        // The shipped Arbiter entry has matchMetadata containing "AtlasBosses/DemonClawBoss".
        var e = cat.ByMetadata("Metadata/Monsters/AtlasBosses/DemonClawBoss/arbiter_01");
        Assert.NotNull(e);
        Assert.Equal("arbiter_of_ash", e!.Key);
    }

    [Fact]
    public void ByMetadata_no_hit_returns_null()
    {
        var cat = BossEncounterCatalog.Shared;
        Assert.Null(cat.ByMetadata("Metadata/Terrain/G1_1"));
    }

    [Fact]
    public void Entries_ordered_as_authored_in_json()
    {
        var cat = BossEncounterCatalog.Shared;
        // First authored entry is the Arbiter.
        Assert.Equal("arbiter_of_ash", cat.Entries[0].Key);
    }
}
