using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using Xunit;
using RawAffix = POE2Radar.Core.Game.Poe2Live.RawAffix;
using InventoryItem = POE2Radar.Core.Game.Poe2Live.InventoryItem;

namespace POE2Radar.Tests.Game;

public class LiveCountersTests
{
    private static ItemFilterEngine EmptyEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var e = new ItemFilterEngine(path);
        e.Replace(Array.Empty<FilterRule>());
        return e;
    }

    private static RawAffix Aff(string modId, params int[] values) => new(modId, values);
    
    private static InventoryItem Item(int inventoryId, string name, string rarity, bool identified, params RawAffix[] affixes)
        => new(name, rarity, identified, inventoryId, affixes);

    private static Poe2Live.EntityDot GroundItem(params RawAffix[] affixes)
        => new(0, 0, default, default, Poe2Live.EntityCategory.Other, "Metadata/Items/Test",
               0, 0, false, 0, Poe2Live.Rarity.Rare, false,
               ItemAffixes: affixes);

    [Fact]
    public void Split_by_InventoryId_1_is_bag_others_are_equipped()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true, Aff("EnergyShield_1", 15)),  // Main bag, matching
            Item(1, "Item2", "Rare", true, Aff("EnergyShield_1", 12)),  // Main bag, matching
            Item(2, "Item3", "Rare", true, Aff("EnergyShield_1", 20)),  // Equipment, matching
            Item(11, "Item4", "Rare", true, Aff("Life_1", 50)),         // Belt, not matching
        };

        var (equipped, inventory) = RadarApp.CountInventoryFilterMatches(items, engine);
        
        Assert.Equal(1, equipped);   // InventoryId 2 = equipment
        Assert.Equal(2, inventory);  // InventoryId 1 = bag items
    }

    [Fact]
    public void Empty_inventory_yields_zero_counts()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var (equipped, inventory) = RadarApp.CountInventoryFilterMatches(null, engine);

        Assert.Equal(0, equipped);
        Assert.Equal(0, inventory);

        var (equipped2, inventory2) = RadarApp.CountInventoryFilterMatches(Array.Empty<InventoryItem>(), engine);
        
        Assert.Equal(0, equipped2);
        Assert.Equal(0, inventory2);
    }

    [Fact]
    public void Disabled_filter_matches_nothing()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", false, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true, Aff("EnergyShield_1", 15)),  // Main bag, matching but filter disabled
            Item(2, "Item2", "Rare", true, Aff("EnergyShield_1", 20)),  // Equipment, matching but filter disabled
        };

        var (equipped, inventory) = RadarApp.CountInventoryFilterMatches(items, engine);
        
        Assert.Equal(0, equipped);
        Assert.Equal(0, inventory);
    }

    [Fact]
    public void No_affixes_never_matches()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true),  // Main bag, no affixes
            Item(2, "Item2", "Rare", true),  // Equipment, no affixes
        };

        var (equipped, inventory) = RadarApp.CountInventoryFilterMatches(items, engine);

        Assert.Equal(0, equipped);
        Assert.Equal(0, inventory);
    }

    [Fact]
    public void PerFilter_attributes_matches_to_all_matching_filters()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("A", "A high", true,  "#FF0000", 100,
            new[] { new FilterRequirement("Life", ">=", 60, null, null, null) }));
        engine.Add(new FilterRule("B", "B low",  true,  "#00FF00",  50,
            new[] { new FilterRequirement("Life", ">=", 40, null, null, null) }));

        var items = new List<InventoryItem>
        {
            Item(1, "LifeItem", "Rare", true, Aff("Life_1", 70)),  // Main bag, matches BOTH filters
        };

        var (gr, eq, inv) = RadarApp.CountPerFilterMatches(
            Array.Empty<Poe2Live.EntityDot>(), items, engine);

        Assert.Empty(gr);
        Assert.Empty(eq);
        Assert.Equal(1, inv["A"]);
        Assert.Equal(1, inv["B"]);
    }

    [Fact]
    public void PerFilter_splits_ground_vs_equipped_vs_inventory()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("X", "X", true, "#FF0000", 100,
            new[] { new FilterRequirement("EnergyShield", ">=", 10, null, null, null) }));

        var entities = new List<Poe2Live.EntityDot>
        {
            GroundItem(Aff("EnergyShield_1", 15)),  // ground
        };
        var items = new List<InventoryItem>
        {
            Item(1, "BagItem",  "Rare", true, Aff("EnergyShield_1", 20)),  // inventory bucket
            Item(2, "BodyItem", "Rare", true, Aff("EnergyShield_1", 25)),  // equipped bucket
        };

        var (gr, eq, inv) = RadarApp.CountPerFilterMatches(entities, items, engine);

        Assert.Equal(1, gr["X"]);
        Assert.Equal(1, eq["X"]);
        Assert.Equal(1, inv["X"]);
    }

    [Fact]
    public void PerFilter_omits_disabled_filters()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("A", "Enabled",  true,  "#FF0000", 100,
            new[] { new FilterRequirement("Life", ">=", 40, null, null, null) }));
        engine.Add(new FilterRule("B", "Disabled", false, "#00FF00",  50,
            new[] { new FilterRequirement("Life", ">=", 40, null, null, null) }));

        var items = new List<InventoryItem>
        {
            Item(1, "LifeItem", "Rare", true, Aff("Life_1", 70)),  // Would match BOTH if both enabled
        };

        var (_, _, inv) = RadarApp.CountPerFilterMatches(
            Array.Empty<Poe2Live.EntityDot>(), items, engine);

        Assert.True(inv.ContainsKey("A"));
        Assert.False(inv.ContainsKey("B"));
        Assert.Equal(1, inv["A"]);
    }
}