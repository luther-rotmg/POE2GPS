using POE2Radar.Core.Diagnostics;

namespace POE2Radar.Core.Diagnostics;

/// <summary>StdVector-like shape: First, Last, and End pointers read from a candidate offset.</summary>
public readonly record struct StdVecShape(nint First, nint Last, nint End);

/// <summary>Grid position as a tuple of two ints (X, Y) — matches StdTuple2D&lt;int&gt;.</summary>
public readonly record struct GridPosShape(int X, int Y);

/// <summary>
/// Diagnostic prober for AtlasNode fields. Sweeps candidate offsets for 3 AtlasNode fields
/// (ConnectionsVec, GridPos, Biome) and returns raw sweep results. Probe-only (no auto-heal,
/// no HealthState hook). B3b will add auto-heal on top of ConnectionsVec + GridPos;
/// B3c will add Biome heal on top.
/// </summary>
public static class AtlasGraphProber
{
    /// <summary>
    /// Sweep ConnectionsVec (StdVector-like) at candidate offsets [0x400..0x800] step 8.
    /// Reads 24 bytes (3 × nint: First, Last, End). Signature-pass if First != 0
    /// AND bytes &gt; 0 AND bytes % 20 == 0 AND count in [1..64].
    /// </summary>
    /// <param name="firstNode">AtlasNode element address (0 = not available).</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 129 ProbeSample&lt;StdVecShape&gt; (empty when firstNode == 0).</returns>
    public static ProbeSample<StdVecShape>[] SweepConnectionsVec(nint firstNode, MemoryReader r)
    {
        if (firstNode == 0) return Array.Empty<ProbeSample<StdVecShape>>();

        var result = new List<ProbeSample<StdVecShape>>(129);
        for (var off = 0x400; off <= 0x800; off += 8)
        {
            var target = firstNode + off;
            var buf = new byte[24];
            var bytesRead = r.TryReadBytes(target, buf);
            if (bytesRead < 24)
            {
                result.Add(new ProbeSample<StdVecShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false));
                continue;
            }

            var first = (nint)BitConverter.ToInt64(buf, 0);
            var last = (nint)BitConverter.ToInt64(buf, 8);
            var end = (nint)BitConverter.ToInt64(buf, 16);
            var vecBytes = (long)last - (long)first;
            var count = vecBytes / 20;

            // Signature pass: First != 0, bytes > 0, bytes % 20 == 0, count in [1..64]
            var passes = first != 0 && vecBytes > 0 && vecBytes % 20 == 0
                         && count >= 1 && count <= 64;

            result.Add(new ProbeSample<StdVecShape>(
                $"0x{off:X}", $"0x{target:X}",
                new StdVecShape(first, last, end), null, passes));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sweep GridPos (StdTuple2D&lt;int&gt;) at candidate offsets [0x300..0x35C] step 4.
    /// Reads 8 bytes (2 × int: X, Y). Signature-pass if |X| &lt;= 512 AND |Y| &lt;= 512.
    /// </summary>
    /// <param name="firstNode">AtlasNode element address (0 = not available).</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 24 ProbeSample&lt;GridPosShape&gt; (empty when firstNode == 0).</returns>
    public static ProbeSample<GridPosShape>[] SweepGridPos(nint firstNode, MemoryReader r)
    {
        if (firstNode == 0) return Array.Empty<ProbeSample<GridPosShape>>();

        var result = new List<ProbeSample<GridPosShape>>(24);
        for (var off = 0x300; off <= 0x35C; off += 4)
        {
            var target = firstNode + off;

            if (!r.TryReadStruct<int>(target, out var x))
            {
                result.Add(new ProbeSample<GridPosShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false));
                continue;
            }

            if (!r.TryReadStruct<int>(target + 4, out var y))
            {
                result.Add(new ProbeSample<GridPosShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false));
                continue;
            }

            // Signature pass: |X| <= 512 AND |Y| <= 512
            var passes = Math.Abs(x) <= 512 && Math.Abs(y) <= 512;

            result.Add(new ProbeSample<GridPosShape>(
                $"0x{off:X}", $"0x{target:X}",
                new GridPosShape(x, y), null, passes));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sweep Biome (unsigned byte) at candidate offsets [0x300..0x340] step 4.
    /// Reads 1 byte. Signature-pass if value in [0..12] (13 known biomes).
    /// </summary>
    /// <param name="firstNode">AtlasNode element address (0 = not available).</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 17 ProbeSample&lt;int&gt; (empty when firstNode == 0).</returns>
    public static ProbeSample<int>[] SweepBiome(nint firstNode, MemoryReader r)
    {
        if (firstNode == 0) return Array.Empty<ProbeSample<int>>();

        var result = new List<ProbeSample<int>>(17);
        for (var off = 0x300; off <= 0x340; off += 4)
        {
            var target = firstNode + off;

            if (!r.TryReadStruct<byte>(target, out var b))
            {
                result.Add(new ProbeSample<int>(
                    $"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                continue;
            }

            var value = (int)b;

            // Signature pass: value in [0..12] (13 known biomes)
            var passes = value >= 0 && value <= 12;

            result.Add(new ProbeSample<int>(
                $"0x{off:X}", $"0x{target:X}", value, null, passes));
        }
        return result.ToArray();
    }
}