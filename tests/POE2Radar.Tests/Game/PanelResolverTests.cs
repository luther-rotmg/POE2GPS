using System;
using System.Collections.Generic;
using System.Reflection;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Game;

public class PanelResolverTests
{
    // Since we can't easily mock the private methods and MemoryReader dependencies,
    // we'll test the public API surface and verify the code compiles and integrates properly
    
    [Fact]
    public void PanelOffsets_Exist_In_Poe2Offsets()
    {
        // Verify that all the required constants exist
        Assert.Equal(986f, Poe2.Panels.PanelWidthUnscaled);
        Assert.Equal(1600f, Poe2.Panels.PanelHeightUnscaled);
        Assert.Equal(33, Poe2.Panels.CharacterPanel_IdxHint);
        Assert.Equal(34, Poe2.Panels.InventoryPanel_IdxHint);
        Assert.Equal(35, Poe2.Panels.StashPanel_IdxHint);
        Assert.Equal(0.80f, Poe2.Panels.StashBottomBarRyMin);
        Assert.Equal(0.85f, Poe2.Panels.StashBottomBarRyMax);
        Assert.Equal(0.60f, Poe2.Panels.StashBottomBarRwMin);
        Assert.Equal(0.80f, Poe2.Panels.StashBottomBarRwMax);
        Assert.Equal(0.008f, Poe2.Panels.InventoryGridRx);
        Assert.Equal(0.554f, Poe2.Panels.InventoryGridRy);
        Assert.Equal(0.984f, Poe2.Panels.InventoryGridRw);
        Assert.Equal(0.244f, Poe2.Panels.InventoryGridRh);
        Assert.Equal(0.03f, Poe2.Panels.FingerprintTolerance);
    }

    [Fact]
    public void Poe2Live_Has_Panel_Methods()
    {
        // Verify that the methods exist (compilation check)
        var type = typeof(Poe2Live);
        Assert.NotNull(type.GetMethod("TryFindCharacterPanel", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("TryFindInventoryPanel", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetMethod("TryFindStashPanel", BindingFlags.Public | BindingFlags.Instance));
    }
}