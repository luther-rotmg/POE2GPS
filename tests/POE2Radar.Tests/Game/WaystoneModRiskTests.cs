using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

/// <summary>
/// Reach — CHOR-41 (v0.26). Locks the parse contract:
/// - non-waystone text returns IsWaystone=false and empty mods
/// - deadly mods are surfaced with the right tier
/// - combos fire when 2+ keys hit
/// - total score aggregates weights + combo bonuses
/// - ShouldSkip flips at the threshold
/// </summary>
public class WaystoneModRiskTests
{
    [Fact]
    public void Empty_input_returns_not_a_waystone()
    {
        var r = WaystoneModRisk.Shared.Parse("");
        Assert.False(r.IsWaystone);
        Assert.Empty(r.Mods);
        Assert.Equal(0, r.TotalScore);
        Assert.False(r.ShouldSkip);
    }

    [Fact]
    public void Non_waystone_item_text_returns_not_a_waystone()
    {
        var r = WaystoneModRisk.Shared.Parse("Item Class: Gems\nRarity: Normal\nFireball");
        Assert.False(r.IsWaystone);
    }

    [Fact]
    public void Waystone_header_is_recognized()
    {
        var r = WaystoneModRisk.Shared.Parse("Item Class: Waystones\nRarity: Rare\n--------\nWaystone Tier: 15");
        Assert.True(r.IsWaystone);
        Assert.Equal("Rare", r.Rarity);
        Assert.Equal(15, r.Tier);
    }

    [Fact]
    public void Phys_reflect_mod_scores_deadly()
    {
        var blob = "Item Class: Waystones\nRarity: Rare\n--------\nMonsters reflect 15% of Physical Damage\n--------\nWaystone Tier: 15";
        var r = WaystoneModRisk.Shared.Parse(blob);
        Assert.True(r.IsWaystone);
        Assert.Contains(r.Mods, m => m.ModKey == "phys_reflect");
        var phys = r.Mods.First(m => m.ModKey == "phys_reflect");
        Assert.Equal(WaystoneModRisk.RiskTier.Deadly, phys.Tier);
    }

    [Fact]
    public void No_leech_plus_no_regen_triggers_combo_and_should_skip()
    {
        var blob =
@"Item Class: Waystones
Rarity: Rare
--------
Monsters cannot be Leeched from
Players cannot Regenerate Life, Mana or Energy Shield
--------
Waystone Tier: 15";
        var r = WaystoneModRisk.Shared.Parse(blob);
        Assert.True(r.IsWaystone);
        Assert.Contains(r.Mods, m => m.ModKey == "no_leech");
        Assert.Contains(r.Mods, m => m.ModKey == "no_regen");
        Assert.NotEmpty(r.Combos);
        Assert.Contains(r.Combos, c => c.Label.Contains("Sustain locked"));
        Assert.True(r.ShouldSkip, $"expected ShouldSkip on total {r.TotalScore}; combos: {string.Join(',', r.Combos.Select(c => c.Label))}");
    }

    [Fact]
    public void Duplicate_mod_matches_are_deduped_by_key()
    {
        // The clipboard sometimes has the same line twice in different sections. Only one entry.
        var blob = "Item Class: Waystones\nRarity: Rare\nMonsters cannot be Leeched from\nMonsters cannot be Leeched from";
        var r = WaystoneModRisk.Shared.Parse(blob);
        Assert.True(r.IsWaystone);
        Assert.Equal(1, r.Mods.Count(m => m.ModKey == "no_leech"));
    }

    [Fact]
    public void Iir_iiq_are_safe_tier()
    {
        var blob = "Item Class: Waystones\nRarity: Rare\n50% increased Item Rarity\n30% increased Item Quantity";
        var r = WaystoneModRisk.Shared.Parse(blob);
        Assert.True(r.IsWaystone);
        Assert.All(r.Mods.Where(m => m.ModKey is "iir" or "iiq"), m => Assert.Equal(WaystoneModRisk.RiskTier.Safe, m.Tier));
        Assert.False(r.ShouldSkip);
    }

    [Fact]
    public void Rules_load_from_embedded_json()
    {
        var w = WaystoneModRisk.Shared;
        Assert.True(w.RuleCount > 0);
        Assert.True(w.ComboCount > 0);
    }
}
