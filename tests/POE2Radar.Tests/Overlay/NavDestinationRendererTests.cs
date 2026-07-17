using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using POE2Radar.Core.NavDestinations;
using POE2Radar.Overlay;
using Xunit;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// v0.41 C2: state management and zone-change cache behavior for the Nav Destinations renderer.
/// These tests verify the public property, the RefreshNavDestinations method, and the zone-filter
/// logic without requiring a Direct2D rendering environment. Pattern follows RuleEffectApplierTests
/// (reflection-based structural checks) and NavDestinationStoreTests (helper methods).
/// </summary>
public sealed class NavDestinationRendererTests
{
    // ── Helpers ──

    private static NavDestination MakeDest(Guid id, string zoneCode, string name, int x = 100, int y = 200)
        => new(id, zoneCode, name, x, y);

    /// <summary>
    /// Create an OverlayRenderer via reflection, passing null for the window (no rendering is done).
    /// </summary>
    private static OverlayRenderer CreateRenderer()
    {
        var ctor = typeof(OverlayRenderer).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
        Assert.NotNull(ctor);
        return (OverlayRenderer)ctor.Invoke(new object?[] { null });
    }

    /// <summary>
    /// Read a private instance field via reflection.
    /// </summary>
    private static T? GetField<T>(OverlayRenderer renderer, string name)
    {
        var field = typeof(OverlayRenderer).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T?)field.GetValue(renderer);
    }

    /// <summary>
    /// Simulate the zone-filter logic from the draw block to verify exact-match filtering.
    /// Returns only destinations whose ZoneCode matches the current zone (exact, case-sensitive).
    /// </summary>
    private static NavDestination[] FilterForZone(IReadOnlyList<NavDestination> destinations, string? zoneCode)
    {
        var currentZone = zoneCode ?? "";
        return destinations.Where(d => d.ZoneCode == currentZone).ToArray();
    }

    // ── Structural property tests ──

    [Fact]
    public void NavDestinations_DefaultEmpty()
    {
        var prop = typeof(OverlayRenderer).GetProperty("NavDestinations",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<NavDestination>), prop.PropertyType);

        var renderer = CreateRenderer();
        var value = (IReadOnlyList<NavDestination>?)prop.GetValue(renderer);
        Assert.NotNull(value);
        Assert.Empty(value);
    }

    [Fact]
    public void RefreshNavDestinations_MethodExists()
    {
        var method = typeof(OverlayRenderer).GetMethod("RefreshNavDestinations",
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(IReadOnlyList<NavDestination>) });
        Assert.NotNull(method);
    }

    [Fact]
    public void RefreshNavDestinations_UpdatesProperty()
    {
        var renderer = CreateRenderer();
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
            MakeDest(Guid.NewGuid(), "ZoneB", "boss"),
        };

        renderer.RefreshNavDestinations(dests);

        var prop = typeof(OverlayRenderer).GetProperty("NavDestinations",
            BindingFlags.Public | BindingFlags.Instance)!;
        var value = (IReadOnlyList<NavDestination>?)prop.GetValue(renderer);
        Assert.NotNull(value);
        Assert.Equal(2, value.Count);
        Assert.Contains(value, d => d.Name == "chest room");
        Assert.Contains(value, d => d.Name == "boss");
    }

    [Fact]
    public void RefreshNavDestinations_ResetsCache()
    {
        // Set up the renderer with some destinations, then simulate a first filter pass
        // by setting _activeNavDestinations to a non-null value. After RefreshNavDestinations
        // the cache should be null again.
        var renderer = CreateRenderer();
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
        };

        // Set the private field to simulate a cached state
        var cacheField = typeof(OverlayRenderer).GetField("_activeNavDestinations",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheField);
        cacheField.SetValue(renderer, Array.Empty<NavDestination>()); // non-null

        // Verify cache is non-null before refresh
        var before = GetField<NavDestination[]?>(renderer, "_activeNavDestinations");
        Assert.NotNull(before); // not null

        // Refresh resets the cache
        renderer.RefreshNavDestinations(dests);

        var after = GetField<NavDestination[]?>(renderer, "_activeNavDestinations");
        Assert.Null(after); // reset to null
    }

    [Fact]
    public void NavDestinations_PropertyType_Correct()
    {
        var prop = typeof(OverlayRenderer).GetProperty("NavDestinations",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<NavDestination>), prop.PropertyType);
        Assert.True(prop.CanRead);
        Assert.True(prop.CanWrite); // has public setter
    }

    // ── Zone-filter behavior tests (via helper that mirrors the draw-block logic) ──

    [Fact]
    public void ZoneFilter_OnlyMatchingZoneCode()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room", 145, 220),
            MakeDest(Guid.NewGuid(), "ZoneB", "boss", 300, 400),
            MakeDest(Guid.NewGuid(), "ZoneA", "altar", 50, 180),
        };

        var filtered = FilterForZone(dests, "ZoneA");

        Assert.Equal(2, filtered.Length);
        Assert.All(filtered, d => Assert.Equal("ZoneA", d.ZoneCode));
    }

    [Fact]
    public void ZoneFilter_EmptyZoneCode_ReturnsEmpty()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
            MakeDest(Guid.NewGuid(), "ZoneB", "boss"),
        };

        var filtered = FilterForZone(dests, "");

        Assert.Empty(filtered);
    }

    [Fact]
    public void ZoneFilter_NullZoneCode_ReturnsEmpty()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
        };

        var filtered = FilterForZone(dests, null);

        Assert.Empty(filtered);
    }

    [Fact]
    public void ZoneFilter_NoMatchingDestinations_ReturnsEmptyArray()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
            MakeDest(Guid.NewGuid(), "ZoneB", "boss"),
        };

        var filtered = FilterForZone(dests, "ZoneC");

        Assert.Empty(filtered);
    }

    [Fact]
    public void ZoneFilter_ExactMatchCaseSensitive()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room"),
            MakeDest(Guid.NewGuid(), "zonea", "altar"),  // different case
        };

        var filtered = FilterForZone(dests, "ZoneA");

        Assert.Single(filtered);
        Assert.Equal("ZoneA", filtered[0].ZoneCode);
        Assert.Equal("chest room", filtered[0].Name);
    }

    [Fact]
    public void MultipleDestinations_SameZone_AllMatch()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "T17_Necropolis", "chest room", 145, 220),
            MakeDest(Guid.NewGuid(), "T17_Necropolis", "boss", 300, 400),
            MakeDest(Guid.NewGuid(), "T17_Necropolis", "altar", 50, 180),
            MakeDest(Guid.NewGuid(), "T17_Necropolis", "exit", 600, 700),
        };

        var filtered = FilterForZone(dests, "T17_Necropolis");

        Assert.Equal(4, filtered.Length);
        Assert.All(filtered, d => Assert.Equal("T17_Necropolis", d.ZoneCode));
    }

    [Fact]
    public void MultipleDestinations_DifferentZones_FiltersCorrectly()
    {
        var dests = new List<NavDestination>
        {
            MakeDest(Guid.NewGuid(), "ZoneA", "chest room", 145, 220),
            MakeDest(Guid.NewGuid(), "ZoneB", "boss", 300, 400),
            MakeDest(Guid.NewGuid(), "ZoneA", "altar", 50, 180),
            MakeDest(Guid.NewGuid(), "ZoneC", "exit", 600, 700),
        };

        var zoneA = FilterForZone(dests, "ZoneA");
        var zoneB = FilterForZone(dests, "ZoneB");
        var zoneC = FilterForZone(dests, "ZoneC");

        Assert.Equal(2, zoneA.Length);
        Assert.All(zoneA, d => Assert.Equal("ZoneA", d.ZoneCode));
        Assert.Single(zoneB);
        Assert.Equal("boss", zoneB[0].Name);
        Assert.Single(zoneC);
        Assert.Equal("exit", zoneC[0].Name);
    }
}