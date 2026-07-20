using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for game-render-cadence values (frame counter / frame time / smoothed FPS).
/// Two-shot sampling: read the whole candidate window once, sleep <paramref name="sampleDurationMs"/>,
/// read the whole window again, and report the per-offset delta so a monotonic frame counter
/// surfaces as <c>delta ∈ [15, 300]</c>. Probe-only (no auto-heal, no consumer wiring) — the
/// companion to v0.42 C1's fingerprint-inference throttle heuristic, shipped so a support
/// payload from a user hitting the controller-mode issue can identify the real offset in one
/// round-trip.
/// </summary>
public static class GameFpsProber
{
    /// <summary>Two int readings <c>sampleDurationMs</c> apart, with <c>Delta = Second - First</c>
    /// computed at probe time. Both readings preserved so a reviewer can distinguish a frame
    /// counter (First &amp; Second both large, delta ≈ FPS) from noise (both jump wildly).</summary>
    public readonly record struct GameFpsIntSample(int First, int Second, int Delta);

    /// <summary>Two float readings <c>sampleDurationMs</c> apart, with <c>Delta = Second - First</c>.
    /// Carries frame-time-in-seconds candidates (e.g. <c>1/144 ≈ 0.0069</c>).</summary>
    public readonly record struct GameFpsFloatSample(float First, float Second, float Delta);

    /// <summary>Sweep InGameState int candidates at offsets <c>[0x40..0x400]</c> step 4.
    /// PassesSignature gate: <c>Second &gt; First AND Delta in [15, 300]</c> (monotonic-increment
    /// gate catches frame counters and smoothed-FPS ints alike; 15 FPS min viable, 300 FPS
    /// 144Hz-with-headroom max).</summary>
    public static ProbeSample<GameFpsIntSample>[] SweepInGameStateInt(nint inGameState, MemoryReader r, int sampleDurationMs)
        => SweepInt(inGameState, r, sampleDurationMs, 0x40, 0x400);

    /// <summary>Sweep InGameState float candidates at offsets <c>[0x40..0x400]</c> step 4.
    /// PassesSignature gate: <c>First &gt; 0 AND First &lt; 1 AND |Second - First| &lt; First * 0.25</c>
    /// (frame-time-in-seconds gate: 1/300 ≈ 0.0033 to 1/15 ≈ 0.067 fits (0, 1); frame-time doesn't
    /// vary by more than ~25% between two 1-second-apart samples in normal play).</summary>
    public static ProbeSample<GameFpsFloatSample>[] SweepInGameStateFloat(nint inGameState, MemoryReader r, int sampleDurationMs)
        => SweepFloat(inGameState, r, sampleDurationMs, 0x40, 0x400);

    /// <summary>Sweep Camera int candidates at offsets <c>[0x00..0x200]</c> step 4. Same
    /// PassesSignature gate as <see cref="SweepInGameStateInt"/>.</summary>
    public static ProbeSample<GameFpsIntSample>[] SweepCameraInt(nint camera, MemoryReader r, int sampleDurationMs)
        => SweepInt(camera, r, sampleDurationMs, 0x00, 0x200);

    /// <summary>Sweep Camera float candidates at offsets <c>[0x00..0x200]</c> step 4. Same
    /// PassesSignature gate as <see cref="SweepInGameStateFloat"/>.</summary>
    public static ProbeSample<GameFpsFloatSample>[] SweepCameraFloat(nint camera, MemoryReader r, int sampleDurationMs)
        => SweepFloat(camera, r, sampleDurationMs, 0x00, 0x200);

    private static ProbeSample<GameFpsIntSample>[] SweepInt(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsIntSample>>();

        var offsets = new List<int>();
        for (var off = lo; off <= hi; off += 4) offsets.Add(off);

        var firsts = new int[offsets.Count];
        var firstOk = new bool[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            firstOk[i] = r.TryReadStruct<int>(baseAddr + offsets[i], out firsts[i]);

        // ONE sleep per method invocation, NOT per-offset — sleep once, read the whole window twice.
        Thread.Sleep(sampleDurationMs);

        var seconds = new int[offsets.Count];
        var secondOk = new bool[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            secondOk[i] = r.TryReadStruct<int>(baseAddr + offsets[i], out seconds[i]);

        var result = new ProbeSample<GameFpsIntSample>[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
        {
            var off = offsets[i];
            var target = baseAddr + off;
            if (!firstOk[i] || !secondOk[i])
            {
                result[i] = new ProbeSample<GameFpsIntSample>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false);
                continue;
            }
            var first = firsts[i];
            var second = seconds[i];
            var delta = second - first;
            var passes = second > first && delta >= 15 && delta <= 300;
            result[i] = new ProbeSample<GameFpsIntSample>(
                $"0x{off:X}", $"0x{target:X}",
                new GameFpsIntSample(first, second, delta), null, passes);
        }
        return result;
    }

    private static ProbeSample<GameFpsFloatSample>[] SweepFloat(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsFloatSample>>();

        var offsets = new List<int>();
        for (var off = lo; off <= hi; off += 4) offsets.Add(off);

        var firsts = new float[offsets.Count];
        var firstOk = new bool[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            firstOk[i] = r.TryReadStruct<float>(baseAddr + offsets[i], out firsts[i]);

        // ONE sleep per method invocation, NOT per-offset — sleep once, read the whole window twice.
        Thread.Sleep(sampleDurationMs);

        var seconds = new float[offsets.Count];
        var secondOk = new bool[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            secondOk[i] = r.TryReadStruct<float>(baseAddr + offsets[i], out seconds[i]);

        var result = new ProbeSample<GameFpsFloatSample>[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
        {
            var off = offsets[i];
            var target = baseAddr + off;
            if (!firstOk[i] || !secondOk[i])
            {
                result[i] = new ProbeSample<GameFpsFloatSample>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false);
                continue;
            }
            var first = firsts[i];
            var second = seconds[i];
            var delta = second - first;
            var passes = first > 0 && first < 1 && Math.Abs(second - first) < first * 0.25;
            result[i] = new ProbeSample<GameFpsFloatSample>(
                $"0x{off:X}", $"0x{target:X}",
                new GameFpsFloatSample(first, second, delta), null, passes);
        }
        return result;
    }
}
