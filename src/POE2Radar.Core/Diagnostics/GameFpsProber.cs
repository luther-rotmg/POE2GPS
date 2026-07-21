using System.Threading.Tasks;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for game-render-cadence values (frame counter / frame time / smoothed FPS).
/// Two-shot sampling: read the whole candidate window once, sleep <paramref name="sampleDurationMs"/>,
/// read the whole window again, and report the per-offset delta so a monotonic frame counter
/// surfaces as <c>delta ∈ [15, 300]</c>, or a smoothed FPS integer surfaces as two readings in
/// <c>[15, 300]</c> with small delta. Probe-only (no auto-heal, no consumer wiring) — the
/// companion to v0.42 C1's fingerprint-inference throttle heuristic, shipped so a support
/// payload from a user hitting the controller-mode issue can identify the real offset in one
/// round-trip.
/// </summary>
public static class GameFpsProber
{
    /// <summary>Two int readings <c>sampleDurationMs</c> apart, with <c>Delta = Second - First</c>
    /// computed at probe time. Both readings preserved so a reviewer can distinguish a frame
    /// counter (First &amp; Second both large, delta ≈ FPS) from a smoothed FPS int (both in [15..300],
    /// delta ≈ 0) from noise (both jump wildly).</summary>
    public readonly record struct GameFpsIntSample(int First, int Second, int Delta);

    /// <summary>Two float readings <c>sampleDurationMs</c> apart, with <c>Delta = Second - First</c>.
    /// Carries frame-time-in-seconds candidates (e.g. <c>1/144 ≈ 0.0069</c>).</summary>
    public readonly record struct GameFpsFloatSample(float First, float Second, float Delta);

    // ── Sync API (v0.42.1) — kept for test isolation (Thread.Sleep works fine off the ThreadPool). ──

    /// <summary>Sweep InGameState int candidates at offsets <c>[0x40..0x400]</c> step 4.
    /// PassesSignature: either <c>Second &gt; First AND Delta in [15, 300]</c> (monotonic frame
    /// counter or accumulating stat) OR <c>First in [15, 300] AND Second in [15, 300] AND
    /// |Delta| &lt;= 3</c> (smoothed FPS integer with small tick-over-tick oscillation).</summary>
    public static ProbeSample<GameFpsIntSample>[] SweepInGameStateInt(nint inGameState, MemoryReader r, int sampleDurationMs)
        => SweepInt(inGameState, r, sampleDurationMs, 0x40, 0x400);

    /// <summary>Sweep InGameState float candidates at offsets <c>[0x40..0x400]</c> step 4.
    /// PassesSignature: <c>First &gt; 0 AND First &lt; 1 AND |Second - First| &lt; First * 0.25</c>
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

    // ── Async API (v0.42.3) — uses Task.Delay so the HTTP-request thread hosting the /api/probe/gamefps
    // endpoint doesn't block for the full sampleDurationMs. Callers on the API path SHOULD prefer these. ──

    /// <inheritdoc cref="SweepInGameStateInt(nint, MemoryReader, int)"/>
    public static Task<ProbeSample<GameFpsIntSample>[]> SweepInGameStateIntAsync(nint inGameState, MemoryReader r, int sampleDurationMs)
        => SweepIntAsync(inGameState, r, sampleDurationMs, 0x40, 0x400);

    /// <inheritdoc cref="SweepInGameStateFloat(nint, MemoryReader, int)"/>
    public static Task<ProbeSample<GameFpsFloatSample>[]> SweepInGameStateFloatAsync(nint inGameState, MemoryReader r, int sampleDurationMs)
        => SweepFloatAsync(inGameState, r, sampleDurationMs, 0x40, 0x400);

    /// <inheritdoc cref="SweepCameraInt(nint, MemoryReader, int)"/>
    public static Task<ProbeSample<GameFpsIntSample>[]> SweepCameraIntAsync(nint camera, MemoryReader r, int sampleDurationMs)
        => SweepIntAsync(camera, r, sampleDurationMs, 0x00, 0x200);

    /// <inheritdoc cref="SweepCameraFloat(nint, MemoryReader, int)"/>
    public static Task<ProbeSample<GameFpsFloatSample>[]> SweepCameraFloatAsync(nint camera, MemoryReader r, int sampleDurationMs)
        => SweepFloatAsync(camera, r, sampleDurationMs, 0x00, 0x200);

    // ── Shared int-sweep logic (sync + async share the passes-gate + first/second read code). ──

    private static ProbeSample<GameFpsIntSample>[] SweepInt(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsIntSample>>();
        var (offsets, firsts, firstOk) = ReadIntWindow(baseAddr, r, lo, hi);
        Thread.Sleep(sampleDurationMs);
        return CompleteIntSweep(baseAddr, r, offsets, firsts, firstOk);
    }

    private static async Task<ProbeSample<GameFpsIntSample>[]> SweepIntAsync(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsIntSample>>();
        var (offsets, firsts, firstOk) = ReadIntWindow(baseAddr, r, lo, hi);
        await Task.Delay(sampleDurationMs).ConfigureAwait(false);
        return CompleteIntSweep(baseAddr, r, offsets, firsts, firstOk);
    }

    private static (int[] offsets, int[] firsts, bool[] firstOk) ReadIntWindow(nint baseAddr, MemoryReader r, int lo, int hi)
    {
        var offsetList = new List<int>();
        for (var off = lo; off <= hi; off += 4) offsetList.Add(off);
        var offsets = offsetList.ToArray();
        var firsts = new int[offsets.Length];
        var firstOk = new bool[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            firstOk[i] = r.TryReadStruct<int>(baseAddr + offsets[i], out firsts[i]);
        return (offsets, firsts, firstOk);
    }

    private static ProbeSample<GameFpsIntSample>[] CompleteIntSweep(nint baseAddr, MemoryReader r, int[] offsets, int[] firsts, bool[] firstOk)
    {
        var seconds = new int[offsets.Length];
        var secondOk = new bool[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            secondOk[i] = r.TryReadStruct<int>(baseAddr + offsets[i], out seconds[i]);

        var result = new ProbeSample<GameFpsIntSample>[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
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
            // v0.42.3: two-mode gate. Mode A = monotonic frame counter (or any accumulating counter);
            // Mode B = smoothed FPS integer that oscillates by a couple of frames tick-over-tick.
            var passesMonotonic = second > first && delta >= 15 && delta <= 300;
            var passesSmoothed = first >= 15 && first <= 300 && second >= 15 && second <= 300 && Math.Abs(delta) <= 3;
            var passes = passesMonotonic || passesSmoothed;
            result[i] = new ProbeSample<GameFpsIntSample>(
                $"0x{off:X}", $"0x{target:X}",
                new GameFpsIntSample(first, second, delta), null, passes);
        }
        return result;
    }

    // ── Shared float-sweep logic. ──

    private static ProbeSample<GameFpsFloatSample>[] SweepFloat(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsFloatSample>>();
        var (offsets, firsts, firstOk) = ReadFloatWindow(baseAddr, r, lo, hi);
        Thread.Sleep(sampleDurationMs);
        return CompleteFloatSweep(baseAddr, r, offsets, firsts, firstOk);
    }

    private static async Task<ProbeSample<GameFpsFloatSample>[]> SweepFloatAsync(nint baseAddr, MemoryReader r, int sampleDurationMs, int lo, int hi)
    {
        if (baseAddr == 0) return Array.Empty<ProbeSample<GameFpsFloatSample>>();
        var (offsets, firsts, firstOk) = ReadFloatWindow(baseAddr, r, lo, hi);
        await Task.Delay(sampleDurationMs).ConfigureAwait(false);
        return CompleteFloatSweep(baseAddr, r, offsets, firsts, firstOk);
    }

    private static (int[] offsets, float[] firsts, bool[] firstOk) ReadFloatWindow(nint baseAddr, MemoryReader r, int lo, int hi)
    {
        var offsetList = new List<int>();
        for (var off = lo; off <= hi; off += 4) offsetList.Add(off);
        var offsets = offsetList.ToArray();
        var firsts = new float[offsets.Length];
        var firstOk = new bool[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            firstOk[i] = r.TryReadStruct<float>(baseAddr + offsets[i], out firsts[i]);
        return (offsets, firsts, firstOk);
    }

    private static ProbeSample<GameFpsFloatSample>[] CompleteFloatSweep(nint baseAddr, MemoryReader r, int[] offsets, float[] firsts, bool[] firstOk)
    {
        var seconds = new float[offsets.Length];
        var secondOk = new bool[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            secondOk[i] = r.TryReadStruct<float>(baseAddr + offsets[i], out seconds[i]);

        var result = new ProbeSample<GameFpsFloatSample>[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
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
