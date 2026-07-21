using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using POE2Radar.Core;
using POE2Radar.Core.Diagnostics;
using POE2Radar.Core.Native;
using Xunit;

namespace POE2Radar.Tests.Diagnostics;

/// <summary>
/// Tests for GameFpsProber — diagnostic two-shot sweep for game-render-cadence values.
/// Tests 1-2 use the zero-handle ProcessHandle pattern (every read fails → "read-fail").
/// Tests 3-6 use a real opened handle to the current test process, backed by an unmanaged
/// buffer (Marshal.AllocHGlobal) that the prober reads via NtReadVirtualMemory. A background
/// thread mutates the buffer between the prober's two reads so the second read sees the
/// mutated value — that's how we simulate a live frame counter / frame time advancing.
/// </summary>
public sealed class GameFpsProberTests
{
    private static ProcessHandle CreateZeroHandleProcessHandle()
    {
        // Use reflection to create a ProcessHandle with zero handle for testing
        var type = typeof(ProcessHandle);
        var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, new Type[] { typeof(int), typeof(string), typeof(string), typeof(nint), typeof(uint), typeof(nint) }, null);
        return (ProcessHandle)ctor!.Invoke(new object[] { 0, "", "", IntPtr.Zero, 0u, IntPtr.Zero });
    }

    private static ProcessHandle CreateSelfProcessHandle()
    {
        // Open the current test process with PROCESS_VM_READ so NtReadVirtualMemory succeeds
        // against our own heap. AllocHGlobal addresses are user-mode and readable through this handle.
        var selfPid = (uint)Environment.ProcessId;
        var hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, selfPid);
        if (hProc == 0)
            throw new InvalidOperationException($"OpenProcess(self) failed: {Marshal.GetLastWin32Error()}");
        var type = typeof(ProcessHandle);
        var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, new Type[] { typeof(int), typeof(string), typeof(string), typeof(nint), typeof(uint), typeof(nint) }, null);
        return (ProcessHandle)ctor!.Invoke(new object[] { (int)selfPid, "", "", IntPtr.Zero, 0u, hProc });
    }

    [Fact]
    public void SweepInGameStateInt_NullBase_ReturnsEmpty()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = GameFpsProber.SweepInGameStateInt(0, reader, sampleDurationMs: 0);
        Assert.Empty(result);
    }

    [Fact]
    public void SweepInGameStateInt_ZeroHandleReader_AllReadFail()
    {
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = GameFpsProber.SweepInGameStateInt(0x1000, reader, sampleDurationMs: 0);
        // Offsets [0x40..0x400] step 4 (inclusive) = (0x400 - 0x40) / 4 + 1 = 241 entries
        Assert.Equal(241, result.Length);
        foreach (var sample in result)
        {
            Assert.Equal("read-fail", sample.ReadFailReason);
            Assert.False(sample.PassesSignature);
            Assert.Equal(default(GameFpsProber.GameFpsIntSample), sample.Value);
        }
    }

    [Fact]
    public void SweepInGameStateInt_MonotonicCandidate_PassesSignature()
    {
        var reader = new MemoryReader(CreateSelfProcessHandle());
        // Buffer covers offsets [0x40..0x400] inclusive (need 0x404 bytes for the 0x400 read).
        var buf = Marshal.AllocHGlobal(0x500);
        try
        {
            // Zero the window.
            for (var i = 0; i < 0x500; i++) Marshal.WriteByte(buf + i, 0);
            // Pre-write the first value at offset 0x100 (in-window).
            Marshal.WriteInt32(buf + 0x100, 1000);

            // Mutation thread: write the second value after a short delay so the mutation lands
            // between the prober's read1 and read2 (both reads happen inside Thread.Sleep(dur)).
            var mutated = new ManualResetEventSlim(false);
            var mutator = new Thread(() =>
            {
                Thread.Sleep(5);
                Marshal.WriteInt32(buf + 0x100, 1060);
                mutated.Set();
            });
            mutator.Start();

            var result = GameFpsProber.SweepInGameStateInt(buf, reader, sampleDurationMs: 30);
            Assert.True(mutated.Wait(2000), "mutator thread did not signal within 2s");

            // Sanity: the self-opened handle must be able to read the test process's own heap.
            var at0x100 = result.Single(s => s.OffsetHex == "0x100");
            Assert.Null(at0x100.ReadFailReason);

            // Exactly one offset passes — 0x100 with first=1000, second=1060, delta=60 ∈ [15,300].
            var passes = result.Where(s => s.PassesSignature).ToArray();
            Assert.Single(passes);
            Assert.Equal("0x100", passes[0].OffsetHex);
            Assert.Null(passes[0].ReadFailReason);
            Assert.Equal(1000, passes[0].Value.First);
            Assert.Equal(1060, passes[0].Value.Second);
            Assert.Equal(60, passes[0].Value.Delta);

            // All other offsets read 0 → 0 (Second > First is false) so they fail the signature.
            foreach (var sample in result)
            {
                if (sample.OffsetHex == "0x100") continue;
                Assert.False(sample.PassesSignature);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public void SweepInGameStateInt_NonMonotonic_FailsSignature()
    {
        var reader = new MemoryReader(CreateSelfProcessHandle());
        var buf = Marshal.AllocHGlobal(0x500);
        try
        {
            for (var i = 0; i < 0x500; i++) Marshal.WriteByte(buf + i, 0);
            // First read sees 1000 at 0x100; the mutation makes the second read see 500 (delta = -500).
            Marshal.WriteInt32(buf + 0x100, 1000);

            var mutated = new ManualResetEventSlim(false);
            var mutator = new Thread(() =>
            {
                Thread.Sleep(5);
                Marshal.WriteInt32(buf + 0x100, 500);
                mutated.Set();
            });
            mutator.Start();

            var result = GameFpsProber.SweepInGameStateInt(buf, reader, sampleDurationMs: 30);
            Assert.True(mutated.Wait(2000));

            var at0x100 = result.Single(s => s.OffsetHex == "0x100");
            Assert.Null(at0x100.ReadFailReason);
            Assert.Equal(1000, at0x100.Value.First);
            Assert.Equal(500, at0x100.Value.Second);
            Assert.Equal(-500, at0x100.Value.Delta);
            // Second > First is false → signature fails.
            Assert.False(at0x100.PassesSignature);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public void SweepInGameStateFloat_FrameTimeCandidate_PassesSignature()
    {
        var reader = new MemoryReader(CreateSelfProcessHandle());
        var buf = Marshal.AllocHGlobal(0x500);
        try
        {
            for (var i = 0; i < 0x500; i++) Marshal.WriteByte(buf + i, 0);
            // First read sees 0.0069f at 0x80 (frame time ≈ 144 FPS). Mutation writes 0.0068f
            // (≈ 1.4% jitter) — well within the 25% gate, |Second - First| = 0.0001 < 0.001725.
            Marshal.WriteInt32(buf + 0x80, BitConverter.SingleToInt32Bits(0.0069f));

            var mutated = new ManualResetEventSlim(false);
            var mutator = new Thread(() =>
            {
                Thread.Sleep(5);
                Marshal.WriteInt32(buf + 0x80, BitConverter.SingleToInt32Bits(0.0068f));
                mutated.Set();
            });
            mutator.Start();

            var result = GameFpsProber.SweepInGameStateFloat(buf, reader, sampleDurationMs: 30);
            Assert.True(mutated.Wait(2000));

            var at0x80 = result.Single(s => s.OffsetHex == "0x80");
            Assert.Null(at0x80.ReadFailReason);
            Assert.Equal(0.0069f, at0x80.Value.First);
            Assert.Equal(0.0068f, at0x80.Value.Second);
            Assert.True(at0x80.PassesSignature);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public void SweepInGameStateFloat_OutOfRange_FailsSignature()
    {
        var reader = new MemoryReader(CreateSelfProcessHandle());
        var buf = Marshal.AllocHGlobal(0x500);
        try
        {
            for (var i = 0; i < 0x500; i++) Marshal.WriteByte(buf + i, 0);
            // 1.5f is outside the (0, 1) gate. Leave it stable across both reads (no mutation needed).
            Marshal.WriteInt32(buf + 0x100, BitConverter.SingleToInt32Bits(1.5f));

            var result = GameFpsProber.SweepInGameStateFloat(buf, reader, sampleDurationMs: 1);

            var at0x100 = result.Single(s => s.OffsetHex == "0x100");
            Assert.Null(at0x100.ReadFailReason);
            Assert.Equal(1.5f, at0x100.Value.First);
            Assert.Equal(1.5f, at0x100.Value.Second);
            Assert.False(at0x100.PassesSignature);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ── v0.42.3 fixes ────────────────────────────────────────────────────────────────

    [Fact]
    public void v0423_SweepInt_SmoothedFPSCandidate_PassesSmoothedGate()
    {
        // v0.42.3 signature-gate extension: a smoothed FPS integer that reads identically (or
        // near-identically) at both samples and sits in the [15..300] plausible-FPS range now
        // passes signature. Pre-fix, the monotonic gate required Second > First, which excluded
        // this case entirely.
        var reader = new MemoryReader(CreateSelfProcessHandle());
        var buf = Marshal.AllocHGlobal(0x420);
        try
        {
            for (int i = 0; i < 0x420; i++) Marshal.WriteByte(buf, i, 0);
            // Write 144 at offset 0x100 twice (before + not mutated between reads → smoothed FPS 144)
            Marshal.WriteInt32(buf, 0x100, 144);

            var result = GameFpsProber.SweepInGameStateInt(buf, reader, sampleDurationMs: 1);
            var at0x100 = result.Single(s => s.OffsetHex == "0x100");
            Assert.Null(at0x100.ReadFailReason);
            Assert.Equal(144, at0x100.Value.First);
            Assert.Equal(144, at0x100.Value.Second);
            Assert.Equal(0, at0x100.Value.Delta);
            // Post-v0.42.3: passes via smoothed-FPS gate (first in [15..300], second in [15..300], |delta| <= 3)
            Assert.True(at0x100.PassesSignature,
                "Smoothed FPS 144/144 in [15..300] should pass v0.42.3 smoothed gate");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public void v0423_SweepInt_SmoothedFPSBelowRange_FailsSignature()
    {
        // Regression: smoothed value 10/10 is below the 15 FPS floor → should NOT pass.
        var reader = new MemoryReader(CreateSelfProcessHandle());
        var buf = Marshal.AllocHGlobal(0x420);
        try
        {
            for (int i = 0; i < 0x420; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0x100, 10);

            var result = GameFpsProber.SweepInGameStateInt(buf, reader, sampleDurationMs: 1);
            var at0x100 = result.Single(s => s.OffsetHex == "0x100");
            Assert.Equal(10, at0x100.Value.First);
            Assert.Equal(10, at0x100.Value.Second);
            Assert.False(at0x100.PassesSignature,
                "Value 10 is below the 15 FPS floor and should not pass the smoothed gate");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task v0423_SweepInGameStateIntAsync_ReturnsTaskThatCompletes()
    {
        // v0.42.3 async overload sanity: SweepXxxAsync returns a Task<...> that awaits and yields
        // the same shape as the sync overload.
        var reader = new MemoryReader(CreateZeroHandleProcessHandle());
        var result = await GameFpsProber.SweepInGameStateIntAsync(0x1000, reader, sampleDurationMs: 1);
        Assert.NotNull(result);
        // Every read fails against a zero handle → every sample reports "read-fail".
        Assert.All(result, s => Assert.Equal("read-fail", s.ReadFailReason));
    }
}
