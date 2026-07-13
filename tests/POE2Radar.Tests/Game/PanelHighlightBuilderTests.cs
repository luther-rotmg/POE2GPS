using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using Xunit;
using RawAffix = POE2Radar.Core.Game.Poe2Live.RawAffix;
using InventoryItem = POE2Radar.Core.Game.Poe2Live.InventoryItem;

namespace POE2Radar.Tests.Game;

public class PanelHighlightBuilderTests
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
    
    private static InventoryItem Item(int inventoryId, string name, string rarity, bool identified, int slotStartX, int slotStartY, int slotEndX, int slotEndY, params RawAffix[] affixes)
        => new(name, rarity, identified, inventoryId, affixes)
        {
            SlotStartX = slotStartX,
            SlotStartY = slotStartY,
            SlotEndX = slotEndX,
            SlotEndY = slotEndY
        };

    [Fact]
    public void Empty_snap_returns_empty_list()
    {
        var engine = EmptyEngine();
        var result1 = RadarApp.BuildInventoryHighlights(null, engine, 0, 0, 12, 5);
        Assert.Empty(result1);

        var result2 = RadarApp.BuildInventoryHighlights(Array.Empty<InventoryItem>(), engine, 0, 0, 12, 5);
        Assert.Empty(result2);
    }

    [Fact]
    public void Zero_grid_dims_returns_empty_list()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true, 0, 0, 0, 0, Aff("EnergyShield_1", 15)),  // Main bag, matching
        };

        var result1 = RadarApp.BuildInventoryHighlights(items, engine, 0, 0, 0, 5);
        Assert.Empty(result1);

        var result2 = RadarApp.BuildInventoryHighlights(items, engine, 0, 0, 12, 0);
        Assert.Empty(result2);
    }

    [Fact]
    public void Skips_items_not_in_Main_bag()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "MainBagItem", "Rare", true, 0, 0, 0, 0, Aff("EnergyShield_1", 15)),  // Main bag (InventoryId=1), matching
            Item(2, "BodyArmour", "Rare", true, 0, 0, 0, 0, Aff("EnergyShield_1", 20)),   // Body armour (InventoryId=2), matching but should be skipped
        };

        var result = RadarApp.BuildInventoryHighlights(items, engine, 1859, 0, 12, 5);
        Assert.Single(result);
    }

    [Fact]
    public void Skips_items_with_no_matches()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 100, null, null, null),  // High requirement that won't be met
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true, 0, 0, 0, 0, Aff("EnergyShield_1", 15)),  // Main bag, not matching
        };

        var result = RadarApp.BuildInventoryHighlights(items, engine, 1859, 0, 12, 5);
        Assert.Empty(result);
    }

    [Fact]
    public void Packs_winning_filter_color_at_priority_0()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("low", "Low Priority", true, "#00FF00", 50, new[]
        {
            new FilterRequirement("Life", ">=", 40, null, null, null),
        }));
        engine.Add(new FilterRule("high", "High Priority", true, "#AABBCC", 100, new[]
        {
            new FilterRequirement("Life", ">=", 40, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "LifeItem", "Rare", true, 0, 0, 0, 0, Aff("Life_1", 70)),  // Main bag, matches both filters
        };

        var result = RadarApp.BuildInventoryHighlights(items, engine, 1859, 0, 12, 5);
        Assert.Single(result);
        // Expected packed color for "#AABBCC" is 0xFFAABBCC
        Assert.Equal(0xFFAABBCCu, result[0].Color);
    }

    [Fact]
    public void Cell_rect_computed_via_ComputeInventoryCellRect()
    {
        var engine = EmptyEngine();
        engine.Add(new FilterRule("test", "Test", true, "#FF0000", 100, new[]
        {
            new FilterRequirement("EnergyShield", ">=", 10, null, null, null),
        }));

        var items = new List<InventoryItem>
        {
            Item(1, "Item1", "Rare", true, 0, 0, 0, 0, Aff("EnergyShield_1", 15)),  // Main bag, slot (0,0)
        };

        var result = RadarApp.BuildInventoryHighlights(items, engine, 1859, 0, 12, 5);
        Assert.Single(result);
        
        // Expected values from PanelCellMathTests
        const float expectedX = 1866.888f;
        const float expectedY = 886.4f;
        const float expectedW = 80.852f;
        const float expectedH = 78.08f;
        const float tolerance = 1.0f;

        Assert.True(System.Math.Abs(expectedX - result[0].UnscaledX) <= tolerance);
        Assert.True(System.Math.Abs(expectedY - result[0].UnscaledY) <= tolerance);
        Assert.True(System.Math.Abs(expectedW - result[0].UnscaledW) <= tolerance);
        Assert.True(System.Math.Abs(expectedH - result[0].UnscaledH) <= tolerance);
    }
}