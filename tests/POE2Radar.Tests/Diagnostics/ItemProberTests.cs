using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core;
using POE2Radar.Core.Diagnostics;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// Tests for ItemProber - diagnostic sweep methods for the item leaf-triangle.
/// Tests verify the public API surface, expected sample counts, null-component handling,
/// and signature logic without requiring a live process.
/// Uses reflection to create a ProcessHandle with zero handle (for testing only).
/// </summary>
public sealed class ItemProberTests
{
    private static ProcessHandle CreateZeroHandleProcessHandle()
    {
        // Use reflection to create a ProcessHandle with zero handle for testing
        var type = typeof(ProcessHandle);
        var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, new Type[] { typeof(int), typeof(string), typeof(string), typeof(nint), typeof(uint), typeof(nint) }, null);
        // Constructor signature is (int, string, string, nint, uint, nint) — object[] boxing
        // requires the FOURTH + SIXTH entries to be actual IntPtr (nint == IntPtr), not int.
        return (ProcessHandle)ctor!.Invoke(new object[] { 0, "", "", IntPtr.Zero, 0u, IntPtr.Zero });
    }

    // ── SweepWorldItemItemEntity ──

    [Fact]
    public void SweepWorldItemItemEntity_NullComponent_ReturnsSevenComponentNullSamples()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepWorldItemItemEntity(0, reader);
        Assert.Equal(7, result.Length);
        foreach (var sample in result)
        {
            Assert.Equal("component-null", sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }

    [Fact]
    public void SweepWorldItemItemEntity_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x10..0x40] step 8 = (0x40 - 0x10) / 8 + 1 = 7 entries
        var result = ItemProber.SweepWorldItemItemEntity(0x1000, reader);
        Assert.Equal(7, result.Length);
    }

    [Fact]
    public void SweepWorldItemItemEntity_FirstOffsetIs0x10()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepWorldItemItemEntity(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x10", result[0].OffsetHex);
    }

    [Fact]
    public void SweepWorldItemItemEntity_LastOffsetIs0x40()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepWorldItemItemEntity(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x40", result[^1].OffsetHex);
    }

    // ── SweepModsRarity ──

    [Fact]
    public void SweepModsRarity_NullComponent_ReturnsThirteenComponentNullSamples()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepModsRarity(0, reader);
        Assert.Equal(13, result.Length);
        foreach (var sample in result)
        {
            Assert.Equal("component-null", sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }

    [Fact]
    public void SweepModsRarity_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x80..0xB0] step 4 = (0xB0 - 0x80) / 4 + 1 = 13 entries
        var result = ItemProber.SweepModsRarity(0x1000, reader);
        Assert.Equal(13, result.Length);
    }

    [Fact]
    public void SweepModsRarity_FirstOffsetIs0x80()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepModsRarity(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x80", result[0].OffsetHex);
    }

    [Fact]
    public void SweepModsRarity_LastOffsetIs0xB0()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepModsRarity(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0xB0", result[^1].OffsetHex);
    }

    // ── SweepRenderItemResourcePath ──

    [Fact]
    public void SweepRenderItemResourcePath_NullComponent_ReturnsFiveComponentNullSamples()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepRenderItemResourcePath(0, reader);
        Assert.Equal(5, result.Length);
        foreach (var sample in result)
        {
            Assert.Equal("component-null", sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }

    [Fact]
    public void SweepRenderItemResourcePath_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x20..0x40] step 8 = (0x40 - 0x20) / 8 + 1 = 5 entries
        var result = ItemProber.SweepRenderItemResourcePath(0x1000, reader);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void SweepRenderItemResourcePath_FirstOffsetIs0x20()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepRenderItemResourcePath(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x20", result[0].OffsetHex);
    }

    [Fact]
    public void SweepRenderItemResourcePath_LastOffsetIs0x40()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepRenderItemResourcePath(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x40", result[^1].OffsetHex);
    }

    // ── SweepBaseNameRow ──

    [Fact]
    public void SweepBaseNameRow_NullComponent_ReturnsFiveComponentNullSamples()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepBaseNameRow(0, reader);
        Assert.Equal(5, result.Length);
        foreach (var sample in result)
        {
            Assert.Equal("component-null", sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }

    [Fact]
    public void SweepBaseNameRow_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x30..0x50] step 8 = (0x50 - 0x30) / 8 + 1 = 5 entries
        var result = ItemProber.SweepBaseNameRow(0x1000, reader);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void SweepBaseNameRow_FirstOffsetIs0x30()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepBaseNameRow(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x30", result[0].OffsetHex);
    }

    [Fact]
    public void SweepBaseNameRow_LastOffsetIs0x50()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = ItemProber.SweepBaseNameRow(0x1000, reader);
        Assert.NotEmpty(result);
        Assert.Equal("0x50", result[^1].OffsetHex);
    }

    [Fact]
    public void SweepBaseNameRow_TwoHopChase_ReadFailureAtHop1_RecordsReadFail()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // With a zero-handle reader, all reads fail, so hop1 reads will return 0 (null-pointer)
        var result = ItemProber.SweepBaseNameRow(0x1000, reader);
        Assert.NotEmpty(result);
        // Each sample should have a ReadFailReason (either "read-fail" or "null-pointer" or "hop1-null")
        // The actual reason depends on the read strategy — we just verify it's not null and PassesSignature is false
        foreach (var sample in result)
        {
            Assert.NotNull(sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }
}