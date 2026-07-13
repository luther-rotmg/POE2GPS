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

    [Fact]
    public void MatchesPanelShape_rejects_invisible()
    {
        // Test that invisible panels are rejected for all panel kinds
        const uint invisibleFlags = 0; // No visible bit set
        const float x = 0f, y = 0f, w = 986f, h = 1600f;
        const bool hasStashBottomBar = false;

        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, invisibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Inventory, invisibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Stash, invisibleFlags, x, y, w, h, hasStashBottomBar));
    }

    [Fact]
    public void MatchesPanelShape_rejects_wrong_size()
    {
        // Test that wrong sized panels are rejected for all panel kinds
        const uint visibleFlags = 1u << Poe2.UiElement.FlagVisibleBit;
        const float x = 0f, y = 0f, w = 800f, h = 1200f; // Wrong size
        const bool hasStashBottomBar = false;

        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Inventory, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Stash, visibleFlags, x, y, w, h, hasStashBottomBar));
    }

    [Fact]
    public void MatchesPanelShape_matches_Inventory_right_anchored()
    {
        // Test that inventory panel is correctly identified by right anchor
        const uint visibleFlags = 1u << Poe2.UiElement.FlagVisibleBit;
        const float x = 1859f, y = 0f, w = 986f, h = 1600f; // Right anchored
        const bool hasStashBottomBar = false;

        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.True(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Inventory, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Stash, visibleFlags, x, y, w, h, hasStashBottomBar));
    }

    [Fact]
    public void MatchesPanelShape_matches_Character_left_no_bar()
    {
        // Test that character panel is correctly identified by left anchor and no stash bar
        const uint visibleFlags = 1u << Poe2.UiElement.FlagVisibleBit;
        const float x = 0f, y = 0f, w = 986f, h = 1600f; // Left anchored
        const bool hasStashBottomBar = false;

        Assert.True(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Inventory, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Stash, visibleFlags, x, y, w, h, hasStashBottomBar));
    }

    [Fact]
    public void MatchesPanelShape_matches_Stash_left_with_bar()
    {
        // Test that stash panel is correctly identified by left anchor and stash bar
        const uint visibleFlags = 1u << Poe2.UiElement.FlagVisibleBit;
        const float x = 0f, y = 0f, w = 986f, h = 1600f; // Left anchored
        const bool hasStashBottomBar = true;

        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Inventory, visibleFlags, x, y, w, h, hasStashBottomBar));
        Assert.True(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Stash, visibleFlags, x, y, w, h, hasStashBottomBar));
    }

    [Fact]
    public void MatchesPanelShape_size_tolerance()
    {
        // Test size tolerance (4f tolerance)
        const uint visibleFlags = 1u << Poe2.UiElement.FlagVisibleBit;
        const float x = 0f, y = 0f;
        const bool hasStashBottomBar = false;

        // Within tolerance (986+3, 1600-3)
        Assert.True(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, 989f, 1597f, hasStashBottomBar));

        // Outside tolerance (986+5)
        Assert.False(Poe2Live.MatchesPanelShape(Poe2Live.PanelKind.Character, visibleFlags, x, y, 991f, 1600f, hasStashBottomBar));
    }

    [Fact]
    public void HasStashBottomBar_detects_band_child()
    {
        // Test that stash bottom bar is detected when child is in the right band
        var children = new List<(uint flags, float ry, float rw)>
        {
            (1u << Poe2.UiElement.FlagVisibleBit, 0.812f, 0.704f) // In the band
        };

        Assert.True(Poe2Live.HasStashBottomBar(children));
    }

    [Fact]
    public void HasStashBottomBar_ignores_out_of_band()
    {
        // Test that stash bottom bar is not detected when child is out of band
        var children1 = new List<(uint flags, float ry, float rw)>
        {
            (1u << Poe2.UiElement.FlagVisibleBit, 0.5f, 0.7f) // Out of ry band
        };

        var children2 = new List<(uint flags, float ry, float rw)>
        {
            (1u << Poe2.UiElement.FlagVisibleBit, 0.82f, 0.5f) // Out of rw band
        };

        Assert.False(Poe2Live.HasStashBottomBar(children1));
        Assert.False(Poe2Live.HasStashBottomBar(children2));
    }
}