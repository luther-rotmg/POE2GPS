using System;
using System.Linq;
using System.Reflection;
using POE2Radar.Core;
using POE2Radar.Core.Diagnostics;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// Tests for BuffProber - diagnostic sweep methods for BuffsComponent/StatusEffect chain.
/// Tests verify the public API surface and signature logic without requiring a live process.
/// Uses reflection to create a ProcessHandle with zero handle (for testing only).
/// </summary>
public sealed class BuffProberTests
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
    public void SweepBuffVector_NullBuffsComp_ReturnsEmpty()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = BuffProber.SweepBuffVector(0, reader);
        Assert.Empty(result);
    }

    [Fact]
    public void SweepBuffVector_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x100..0x200] step 8 = (0x200 - 0x100) / 8 + 1 = 33 entries
        var result = BuffProber.SweepBuffVector(0x1000, reader);
        Assert.Equal(33, result.Length);
    }

    [Fact]
    public void SweepBuffVector_SampleHasCorrectStructure()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = BuffProber.SweepBuffVector(0x1000, reader);
        Assert.NotEmpty(result);

        // Each sample should have the expected structure
        foreach (var sample in result)
        {
            Assert.Matches("^0x[0-9A-F]+$", sample.OffsetHex);
            Assert.StartsWith("0x", sample.TargetAddr);
            // With a zero-handle reader, reads should fail → ReadFailReason set, Value default
            Assert.NotNull(sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }

    [Fact]
    public void SweepDefinition_NullStatusEffect_ReturnsEmpty()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = BuffProber.SweepDefinition(0, reader);
        Assert.Empty(result);
    }

    [Fact]
    public void SweepDefinition_ReturnsExpectedSampleCount()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        // Offsets [0x00..0x30] step 8 = 7 entries
        var result = BuffProber.SweepDefinition(0x1000, reader);
        Assert.Equal(7, result.Length);
    }

    [Fact]
    public void SweepDefinition_SampleHasCorrectStructure()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = BuffProber.SweepDefinition(0x1000, reader);
        Assert.NotEmpty(result);

        // Each sample should have the expected structure
        foreach (var sample in result)
        {
            Assert.Matches("^0x[0-9A-F]+$", sample.OffsetHex);
            Assert.StartsWith("0x", sample.TargetAddr);
            // With a zero-handle reader, reads should fail → ReadFailReason set, Value default
            Assert.NotNull(sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
        }
    }
}