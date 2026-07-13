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
    
}