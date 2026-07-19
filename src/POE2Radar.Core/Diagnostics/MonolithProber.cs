using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for RuneStation/monolith device fields. Sweeps candidate offsets for the
/// ListenerSub pointer (device → RuneStation back-pointer test) and the RuneStride integer
/// (anchor-run-row stride) and returns raw sweep results. Probe-only (no auto-heal, no HealthState hook).
/// B4b will add auto-heal + HealthState on top after this lands cleanly.
/// </summary>
public static class MonolithProber
{
    /// <summary>Sweep ListenerSub pointer at candidate offsets [0x80..0xC0] step 8.
    /// Sig-pass when chasing the pointer to +0x00 (sub pointer) gives a station whose Owner field
    /// equals the device address (verifying the back-pointer that establishes the station ↔ device
    /// relationship).</summary>
    /// <param name="device">Monolith device address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;nint&gt;.</returns>
    public static ProbeSample<nint>[] SweepListenerSub(nint device, MemoryReader r)
    {
        if (device == 0) return Array.Empty<ProbeSample<nint>>();

        var result = new List<ProbeSample<nint>>();
        for (var off = 0x80; off <= 0xC0; off += 8)
        {
            var target = device + off;

            if (!r.TryReadStruct<nint>(target, out var nodePtr))
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                continue;
            }

            if (nodePtr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "null-pointer", false));
                continue;
            }

            // Read sub pointer at nodePtr + 0x00 (first field of the listener node structure)
            if (!r.TryReadStruct<nint>(nodePtr, out var sub))
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", nodePtr, "sub-read-fail", false));
                continue;
            }

            if (sub == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", nodePtr, "null-sub", false));
                continue;
            }

            // Compute candidate station = sub - ListenerSub
            var cand = sub - Poe2.RuneStation.ListenerSub;

            // Read Owner field at station + 0x10 to verify back-pointer
            if (!r.TryReadStruct<nint>(cand + Poe2.RuneStation.Owner, out var owner))
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", nodePtr, "owner-read-fail", false));
                continue;
            }

            var passes = owner == device;
            result.Add(new ProbeSample<nint>(
                $"0x{off:X}", $"0x{target:X}", nodePtr, null, passes));
        }
        return result.ToArray();
    }

    /// <summary>Sweep RuneStride integer at candidate offsets {0x60, 0x64, 0x68, 0x6c, 0x70, 0x78}.
    /// Sig-pass if the int value is in [0x40, 0x100] — a reasonable proxy signature for a stride value
    /// that would produce plausible AnchorIdx values in the RuneStationDataPtr structure.</summary>
    /// <param name="listenerSub">The confirmed (sig-passed) ListenerSub pointer value (the station address
    /// minus the ListenerSub offset).</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of ProbeSample&lt;int&gt;.</returns>
    public static ProbeSample<int>[] SweepRuneStride(nint listenerSub, MemoryReader r)
    {
        if (listenerSub == 0) return Array.Empty<ProbeSample<int>>();

        var result = new List<ProbeSample<int>>();
        foreach (var off in new int[] { 0x60, 0x64, 0x68, 0x6c, 0x70, 0x78 })
        {
            var target = listenerSub + off;

            if (!r.TryReadStruct<int>(target, out var stride))
            {
                result.Add(new ProbeSample<int>(
                    $"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                continue;
            }

            // Signature pass: stride in [0x40, 0x100] — plausible stride range
            var passes = stride >= 0x40 && stride <= 0x100;

            result.Add(new ProbeSample<int>(
                $"0x{off:X}", $"0x{target:X}", stride, null, passes));
        }
        return result.ToArray();
    }
}