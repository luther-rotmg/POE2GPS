using POE2Radar.Core.Game;

public class MonsterAffixCatalogTests
{
    static AffixFilter F(AffixTier t, bool all = false, int max = 4, string[]? show = null, string[]? hide = null)
        => new(t, new HashSet<string>(show ?? System.Array.Empty<string>()),
                  new HashSet<string>(hide ?? System.Array.Empty<string>()), all, max);

    [Fact] public void Resolve_prettifies_unknown_id()
    {
        // "MonsterExtraFast99" is NOT in poe2_monster_mod_names.json → hits Prettify fallback
        var a = MonsterAffixCatalog.Shared.Resolve("MonsterExtraFast99");
        Assert.Equal("Extra Fast 99", a.Name);   // Strip "Monster", split camelCase+digit → "Extra Fast 99"
        Assert.Equal(AffixTier.Minor, a.Tier);   // uncurated → Minor
    }

    [Fact] public void Select_tier_threshold_excludes_below()
    {
        // a Minor-tier (prettified) id under a Deadly threshold → not shown
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" }, F(AffixTier.Deadly));
        Assert.Empty(lines);
    }

    [Fact] public void Select_alwaysShow_promotes_below_threshold()
    {
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" },
            F(AffixTier.Deadly, show: new[] { "MonsterExtraFast1" }));
        Assert.Single(lines);
        Assert.Equal("Extra Fast", lines[0].Name);
    }

    [Fact] public void Select_hide_suppresses_even_under_displayAll()
    {
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast1" },
            F(AffixTier.Minor, all: true, hide: new[] { "MonsterExtraFast1" }));
        Assert.Empty(lines);
    }

    [Fact] public void Select_caps_at_maxLines()
    {
        var ids = new[] { "MonsterExtraFast1", "MonsterExtraFast2", "MonsterExtraFast3", "MonsterExtraFast4" };
        var lines = MonsterAffixCatalog.Shared.Select(ids, F(AffixTier.Minor, max: 2));
        Assert.Equal(2, lines.Count);
    }

    [Fact] public void Select_dedupes_by_name_and_orders_deadly_first()
    {
        // two ids resolving to the same prettified name collapse to one line (dedup)
        var lines = MonsterAffixCatalog.Shared.Select(new[] { "MonsterExtraFast99", "MonsterExtraFast99" },
            F(AffixTier.Minor, max: 4));
        Assert.Single(lines);
    }

    [Fact] public void Select_orders_deadly_before_minor()
    {
        // MonsterVolatile1 = curated Deadly; MonsterExtraFast99 = uncurated → Minor
        // Deadly must sort before Minor regardless of input order
        var lines = MonsterAffixCatalog.Shared.Select(
            new[] { "MonsterExtraFast99", "MonsterVolatile1" },
            F(AffixTier.Minor, max: 4));
        Assert.Equal(2, lines.Count);
        Assert.Equal(AffixTier.Deadly, lines[0].Tier);
        Assert.Equal(AffixTier.Minor, lines[1].Tier);
    }
}
