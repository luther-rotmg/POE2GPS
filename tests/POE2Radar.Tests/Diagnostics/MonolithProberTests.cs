using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core;
using POE2Radar.Core.Diagnostics;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// Tests for MonolithProber - diagnostic sweep methods for RuneStation/monolith device fields.
/// Tests verify the public API surface and signature logic without requiring a live process.
/// Uses reflection to create a ProcessHandle with zero handle (for testing only).
/// </summary>
public sealed class MonolithProberTests
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
    public void SweepListenerSub_NullDevice_ReturnsEmpty()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = MonolithProber.SweepListenerSub(0, reader);
        Assert.Empty(result);
    }

    [Fact]
    public void SweepListenerSub_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x80..0xC0] step 8 = (0xC0 - 0x80) / 8 + 1 = 9 entries
        var result = MonolithProber.SweepListenerSub(0x1000, reader);
        Assert.Equal(9, result.Length);
    }

    [Fact]
    public void SweepListenerSub_SampleHasCorrectStructure()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = MonolithProber.SweepListenerSub(0x1000, reader);
        Assert.NotEmpty(result);
        var sample = result[0];
        Assert.Equal("0x80", sample.OffsetHex);
        Assert.StartsWith("0x", sample.TargetAddr);
        Assert.Equal(0L, sample.Value);
        Assert.NotNull(sample.ReadFailReason);
        Assert.False(sample.PassesSignature);
    }

    [Fact]
    public void SweepRuneStride_NullListener_ReturnsEmpty()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = MonolithProber.SweepRuneStride(0, reader);
        Assert.Empty(result);
    }

    [Fact]
    public void SweepRuneStride_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // 6 candidate offsets: {0x60, 0x64, 0x68, 0x6c, 0x70, 0x78}
        var result = MonolithProber.SweepRuneStride(0x1000, reader);
        Assert.Equal(6, result.Length);
    }

    [Fact]
    public void SweepRuneStride_SampleHasCorrectStructure()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = MonolithProber.SweepRuneStride(0x1000, reader);
        Assert.NotEmpty(result);
        var sample = result[0];
        Assert.Equal("0x60", sample.OffsetHex);
        Assert.StartsWith("0x", sample.TargetAddr);
        Assert.Equal(0, sample.Value);
        Assert.NotNull(sample.ReadFailReason);
        Assert.False(sample.PassesSignature);
    }
}