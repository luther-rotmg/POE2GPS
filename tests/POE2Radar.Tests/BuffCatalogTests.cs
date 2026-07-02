using POE2Radar.Core.Game;

public class BuffCatalogTests
{
    static BuffFilter F(BuffTier t, bool all = false, int max = 4, string[]? show = null, string[]? hide = null)
        => new(t, new HashSet<string>(show ?? System.Array.Empty<string>()),
                  new HashSet<string>(hide ?? System.Array.Empty<string>()), all, max);

    static (string, float, bool) B(string id, float timer = 0f, bool perm = true) => (id, timer, perm);

    [Fact] public void Prettify_snake_case_to_title()
        => Assert.Equal("Igniting Presence Aura", BuffCatalog.Prettify("igniting_presence_aura"));

    [Fact] public void Resolve_uncurated_uses_heuristic_tier()
    {
        // "*aura*" heuristic → Notable
        Assert.Equal(BuffTier.Notable, BuffCatalog.Shared.Resolve("some_fire_aura").Tier);
        // enrage → Deadly
        Assert.Equal(BuffTier.Deadly, BuffCatalog.Shared.Resolve("monster_enrage").Tier);
        // plain → Minor
        Assert.Equal(BuffTier.Minor, BuffCatalog.Shared.Resolve("something_plain").Tier);
    }

    [Fact] public void Select_junk_suppressed_by_default_shown_under_displayAll()
    {
        var junk = new[] { B("enemies_in_presence_events_tracker") };
        Assert.Empty(BuffCatalog.Shared.Select(junk, F(BuffTier.Minor)));
        Assert.Single(BuffCatalog.Shared.Select(junk, F(BuffTier.Minor, all: true)));
    }

    [Fact] public void Select_tier_threshold_excludes_below()
        => Assert.Empty(BuffCatalog.Shared.Select(new[] { B("something_plain") }, F(BuffTier.Deadly)));

    [Fact] public void Select_appends_timer_for_temporary_buffs()
    {
        var lines = BuffCatalog.Shared.Select(new[] { B("some_fire_aura", timer: 3.2f, perm: false) }, F(BuffTier.Minor));
        Assert.Single(lines);
        Assert.EndsWith("4s", lines[0].Text);   // ceil(3.2) = 4
        Assert.DoesNotContain("s", lines[0].Text.Replace("4s", ""));  // only the timer suffix carries an 's'... (sanity)
    }

    [Fact] public void Select_permanent_has_no_timer_suffix()
    {
        var lines = BuffCatalog.Shared.Select(new[] { B("some_fire_aura", perm: true) }, F(BuffTier.Minor));
        Assert.False(lines[0].Text.EndsWith("s") && char.IsDigit(lines[0].Text[^2]));
    }

    [Fact] public void Select_caps_at_maxLines_and_orders_deadly_first()
    {
        var buffs = new[] { B("something_plain"), B("monster_enrage"), B("a_shield_x"), B("b_aura_y"), B("c_haste_z") };
        var lines = BuffCatalog.Shared.Select(buffs, F(BuffTier.Minor, max: 2));
        Assert.Equal(2, lines.Count);
        Assert.Equal(BuffTier.Deadly, lines[0].Tier);   // enrage sorts first
    }

    [Fact] public void Select_hide_suppresses_even_under_displayAll()
        => Assert.Empty(BuffCatalog.Shared.Select(new[] { B("some_fire_aura") },
              F(BuffTier.Minor, all: true, hide: new[] { "some_fire_aura" })));
}
