using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core;
using POE2Radar.Core.Diagnostics;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// Tests for UiElementFlagsProber - diagnostic flag-word sweep for atlas-panel UiElement.
/// Tests verify the public API surface without requiring a live process.
/// Uses reflection to create a ProcessHandle with zero handle (for testing only).
/// </summary>
public sealed class UiElementFlagsProberTests
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

    [Fact]
    public void TakeSnapshot_NullAtlasPanel_ReturnsAllZeros()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0, reader);

        Assert.Equal(13, snapshot.WordsPerOffset.Count);
        Assert.Equal(0, snapshot.AtlasPanelAddr);
        foreach (var kv in snapshot.WordsPerOffset)
            Assert.Equal(0u, kv.Value);
    }

    [Fact]
    public void TakeSnapshot_ReturnsExactlyThirteenOffsets()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0x1000, reader);

        Assert.Equal(13, snapshot.WordsPerOffset.Count);
    }

    [Fact]
    public void TakeSnapshot_CoversFullCandidateRange()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0x1000, reader);

        var expected = new[] { 0x170, 0x174, 0x178, 0x17C, 0x180, 0x184, 0x188, 0x18C, 0x190, 0x194, 0x198, 0x19C, 0x1A0 };
        var actual = snapshot.WordsPerOffset.Keys.OrderBy(k => k).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TakeSnapshot_TakenUtcIsRecent()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0x1000, reader);

        Assert.True((DateTime.UtcNow - snapshot.TakenUtc).TotalSeconds < 5);
    }

    [Fact]
    public void TakeSnapshot_AtlasPanelAddrPreserved()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        nint addr = 0x7ffe1234;
        var snapshot = UiElementFlagsProber.TakeSnapshot(addr, reader);

        Assert.Equal(addr, snapshot.AtlasPanelAddr);
    }

    [Fact]
    public void TakeSnapshot_ReadFailure_RecordsZero()
    {
        // A zero-handle reader causes all RPM reads to fail, but TakeSnapshot catches
        // them and records 0u — no exception should surface.
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0x1000, reader);

        Assert.Equal(13, snapshot.WordsPerOffset.Count);
        foreach (var kv in snapshot.WordsPerOffset)
            Assert.Equal(0u, kv.Value);
    }

    [Fact]
    public void FlagsSnapshot_RecordEquality_ByValue()
    {
        var words = new Dictionary<int, uint>
        {
            { 0x170, 0x00000001u },
            { 0x174, 0x00000002u },
        };
        var now = DateTime.UtcNow;
        var a = new FlagsSnapshot(0x1000, now, words);
        var b = new FlagsSnapshot(0x1000, now, words);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void FlagsSnapshot_WordsPerOffset_IsReadOnlyContract()
    {
        var prop = typeof(FlagsSnapshot).GetProperty(nameof(FlagsSnapshot.WordsPerOffset));
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyDictionary<int, uint>), prop!.PropertyType);
    }

    [Fact]
    public void FlagsSnapshot_AllThirteenOffsetsAreDistinct()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0x1000, reader);

        Assert.Equal(13, snapshot.WordsPerOffset.Keys.Distinct().Count());
    }

    [Fact]
    public void TakeSnapshot_NullAtlasPanel_AtlasPanelAddrIsZero()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var snapshot = UiElementFlagsProber.TakeSnapshot(0, reader);

        Assert.Equal(nint.Zero, snapshot.AtlasPanelAddr);
    }
}