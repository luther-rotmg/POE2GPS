using POE2Radar.Core.Diagnostics;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for AreaInstance fields. Reads candidate offsets for 5 key AreaInstance
/// fields and returns raw sweep results. This is probe-only (no auto-heal, no HealthState hook).
/// B1b will add auto-heal + HealthState on top after this lands cleanly.
/// </summary>
public static class AreaInstanceProber
{
    /// <summary>StdMap-like structure: head pointer, size, and is-nil flag.</summary>
    public readonly record struct StdMapShape(nint HeadPtr, int Size, byte IsNilByte);

    /// <summary>Sweep AwakeEntities std::map at candidate offsets [0x6A0..0x720] step 8.</summary>
    /// <param name="areaInstance">AreaInstance base address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;StdMapShape&gt;.</returns>
    public static ProbeSample<StdMapShape>[] SweepAwakeEntities(nint areaInstance, MemoryReader r)
    {
        if (areaInstance == 0) return Array.Empty<ProbeSample<StdMapShape>>();

        var result = new List<ProbeSample<StdMapShape>>();
        for (var off = 0x6A0; off <= 0x720; off += 8)
        {
            var target = areaInstance + off;
            var buf = new byte[16];
            var bytesRead = r.TryReadBytes(target, buf);
            if (bytesRead < 16)
            {
                result.Add(new ProbeSample<StdMapShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false));
                continue;
            }

            var headPtr = (nint)BitConverter.ToInt64(buf, 0);
            var size = BitConverter.ToInt32(buf, 8);
            var isNilByte = buf[12]; // typically at +0x0C for the IsNil byte in node

            // Signature pass: size in [0..100000] AND headPtr != 0
            var passes = size >= 0 && size <= 100000 && headPtr != 0;

            result.Add(new ProbeSample<StdMapShape>(
                $"0x{off:X}", $"0x{target:X}",
                new StdMapShape(headPtr, size, isNilByte), null, passes));
        }
        return result.ToArray();
    }

    /// <summary>Sweep SleepingEntities std::map at candidate offsets [0x6B0..0x720] step 8.</summary>
    /// <param name="areaInstance">AreaInstance base address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;StdMapShape&gt;.</returns>
    public static ProbeSample<StdMapShape>[] SweepSleepingEntities(nint areaInstance, MemoryReader r)
    {
        if (areaInstance == 0) return Array.Empty<ProbeSample<StdMapShape>>();

        var result = new List<ProbeSample<StdMapShape>>();
        for (var off = 0x6B0; off <= 0x720; off += 8)
        {
            var target = areaInstance + off;
            var buf = new byte[16];
            var bytesRead = r.TryReadBytes(target, buf);
            if (bytesRead < 16)
            {
                result.Add(new ProbeSample<StdMapShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail", false));
                continue;
            }

            var headPtr = (nint)BitConverter.ToInt64(buf, 0);
            var size = BitConverter.ToInt32(buf, 8);
            var isNilByte = buf[12];

            // Signature pass: size in [0..100000] AND headPtr != 0
            var passes = size >= 0 && size <= 100000 && headPtr != 0;

            result.Add(new ProbeSample<StdMapShape>(
                $"0x{off:X}", $"0x{target:X}",
                new StdMapShape(headPtr, size, isNilByte), null, passes));
        }
        return result.ToArray();
    }

    /// <summary>Sweep LocalPlayer pointer at candidate offsets [0x5A0..0x5F0] step 8.</summary>
    /// <param name="areaInstance">AreaInstance base address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;nint&gt;.</returns>
    public static ProbeSample<nint>[] SweepLocalPlayer(nint areaInstance, MemoryReader r)
    {
        if (areaInstance == 0) return Array.Empty<ProbeSample<nint>>();

        var result = new List<ProbeSample<nint>>();
        for (var off = 0x5A0; off <= 0x5F0; off += 8)
        {
            var target = areaInstance + off;
            var ptr = r.ReadPointer(target);
            if (ptr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "null-pointer", false));
                continue;
            }

            // Chase to +0x08 for EntityDetailsPtr
            var detailsPtr = r.ReadPointer(ptr + 0x08);
            if (detailsPtr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "no-details", false));
                continue;
            }

            // Chase further to Metadata string (details+0x08 for the string pointer)
            var metaPtr = r.ReadPointer(detailsPtr + 0x08);
            if (metaPtr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "no-metadata", false));
                continue;
            }

            // Read metadata string (UTF-16, up to 64 chars)
            var meta = r.ReadStringUtf16(metaPtr, 64);
            var passes = meta.StartsWith("Metadata/", StringComparison.Ordinal);

            result.Add(new ProbeSample<nint>(
                $"0x{off:X}", $"0x{target:X}", ptr, null, passes));
        }
        return result.ToArray();
    }

    /// <summary>Sweep ServerDataPtr pointer at candidate offsets [0x580..0x5D0] step 8.</summary>
    /// <param name="areaInstance">AreaInstance base address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;nint&gt;.</returns>
    public static ProbeSample<nint>[] SweepServerDataPtr(nint areaInstance, MemoryReader r)
    {
        if (areaInstance == 0) return Array.Empty<ProbeSample<nint>>();

        var result = new List<ProbeSample<nint>>();
        for (var off = 0x580; off <= 0x5D0; off += 8)
        {
            var target = areaInstance + off;
            var ptr = r.ReadPointer(target);
            if (ptr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "null-pointer", false));
                continue;
            }

            // Chase to +0x48 for StdVector
            var vecTarget = ptr + 0x48;
            var vecBuf = new byte[24];
            var bytesRead = r.TryReadBytes(vecTarget, vecBuf);
            if (bytesRead < 24)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "vec-read-fail", false));
                continue;
            }

            var vecFirst = (nint)BitConverter.ToInt64(vecBuf, 0);
            var vecLast = (nint)BitConverter.ToInt64(vecBuf, 8);
            var count = (int)(((long)vecLast - (long)vecFirst) / 8);

            // Signature pass: first pointer non-zero AND count in [1..4]
            var passes = vecFirst != 0 && count >= 1 && count <= 4;

            result.Add(new ProbeSample<nint>(
                $"0x{off:X}", $"0x{target:X}", ptr, null, passes));
        }
        return result.ToArray();
    }

    /// <summary>Sweep TerrainMetadata at candidate offsets [0x880..0x900] step 8.</summary>
    /// <param name="areaInstance">AreaInstance base address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;int&gt;.</returns>
    public static ProbeSample<int>[] SweepTerrainMetadata(nint areaInstance, MemoryReader r)
    {
        if (areaInstance == 0) return Array.Empty<ProbeSample<int>>();

        var result = new List<ProbeSample<int>>();
        for (var off = 0x880; off <= 0x900; off += 8)
        {
            var target = areaInstance + off;

            // Read TotalTiles at +0x00 within TerrainStruct (or at the candidate offset directly)
            if (!r.TryReadStruct<int>(target, out var totalTiles))
            {
                result.Add(new ProbeSample<int>(
                    $"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                continue;
            }

            // Signature pass: TotalTiles in [1..1_000_000]
            var passes = totalTiles >= 1 && totalTiles <= 1_000_000;

            result.Add(new ProbeSample<int>(
                $"0x{off:X}", $"0x{target:X}", totalTiles, null, passes));
        }
        return result.ToArray();
    }
}
