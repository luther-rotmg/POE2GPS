using System.Text.RegularExpressions;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for the BuffsComponent → StatusEffect chain. Sweeps candidate offsets for the
/// BuffVector (StdVector base) on the BuffsComponent and for the Definition pointer on the first
/// StatusEffect entry. Probe-only (no auto-heal, no HealthState hook). B8b will add auto-heal +
/// HealthState on top after this lands cleanly.
/// </summary>
public static class BuffProber
{
    /// <summary>
    /// Sweep all qword slots in [0x100..0x200] (33 candidates) on the BuffsComponent struct,
    /// reading each as a StdVector-shape record. Sig-pass when:
    ///   - First != 0
    ///   - DerivedCount ∈ [1, 128] (computed as (Last - First) / 8)
    ///   - The chased Owner pointer at First + 0x00 equals buffsComp (back-pointer match)
    /// </summary>
    /// <param name="buffsComp">BuffsComponent address.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 33 ProbeSample&lt;StdVecShape&gt;.</returns>
    public static ProbeSample<StdVecShape>[] SweepBuffVector(nint buffsComp, MemoryReader r)
    {
        if (buffsComp == 0) return Array.Empty<ProbeSample<StdVecShape>>();

        var result = new List<ProbeSample<StdVecShape>>(33);
        for (var off = 0x100; off <= 0x200; off += 8)
        {
            var target = buffsComp + off;

            // Read First
            if (!r.TryReadStruct<nint>(target, out var first))
            {
                result.Add(new ProbeSample<StdVecShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail-first", false));
                continue;
            }

            // Read Last
            if (!r.TryReadStruct<nint>(target + 8, out var last))
            {
                result.Add(new ProbeSample<StdVecShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail-last", false));
                continue;
            }

            // Read End
            if (!r.TryReadStruct<nint>(target + 16, out var end))
            {
                result.Add(new ProbeSample<StdVecShape>(
                    $"0x{off:X}", $"0x{target:X}", default, "read-fail-end", false));
                continue;
            }

            var shape = new StdVecShape(first, last, end);
            var derivedCount = first != 0 && last != 0
                ? (int)((long)(last - first) / 8)
                : 0;

            string? failReason = null;
            bool passesSignature = false;

            if (first == 0)
            {
                failReason = "null-first";
            }
            else if (derivedCount < 1 || derivedCount > 128)
            {
                failReason = derivedCount == 0
                    ? "zero-derived-count"
                    : $"derived-count-out-of-range:{derivedCount}";
            }
            else
            {
                // Back-pointer check: chase First to read Owner at StatusEffect + 0x00
                if (!r.TryReadStruct<nint>(first, out var owner))
                {
                    failReason = "owner-read-fail";
                }
                else if (owner == buffsComp)
                {
                    passesSignature = true;
                }
                else
                {
                    failReason = $"back-ptr-mismatch:owner=0x{owner:X}";
                }
            }

            result.Add(new ProbeSample<StdVecShape>(
                $"0x{off:X}", $"0x{target:X}", shape, failReason, passesSignature));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Sweep all qword slots in [0x00..0x30] (7 candidates) on the first StatusEffect struct,
    /// reading each as a pointer that may point to a BuffDefinition whose first qword is a
    /// UTF-16 buff-id string pointer. Sig-pass when the decoded UTF-16 string matches
    /// <c>^[a-z0-9_]{3,}$</c>.
    /// </summary>
    /// <param name="firstStatusEffect">Address of the first StatusEffect in the buff vector.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 7 ProbeSample&lt;nint&gt;. Value is the pointer at each candidate offset.</returns>
    public static ProbeSample<nint>[] SweepDefinition(nint firstStatusEffect, MemoryReader r)
    {
        if (firstStatusEffect == 0) return Array.Empty<ProbeSample<nint>>();

        var result = new List<ProbeSample<nint>>(7);
        for (var off = 0x00; off <= 0x30; off += 8)
        {
            var target = firstStatusEffect + off;

            if (!r.TryReadStruct<nint>(target, out var ptr))
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                continue;
            }

            if (ptr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", 0, "null-pointer", false));
                continue;
            }

            // Chase pointer to read the first qword (→ BuffDefinition.IdPtr)
            if (!r.TryReadStruct<nint>(ptr, out var idPtr))
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "idptr-read-fail", false));
                continue;
            }

            if (idPtr == 0)
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "null-idptr", false));
                continue;
            }

            // Read the UTF-16 buff-id string
            string id;
            try
            {
                id = r.ReadStringUtf16(idPtr, 128);
            }
            catch
            {
                result.Add(new ProbeSample<nint>(
                    $"0x{off:X}", $"0x{target:X}", ptr, "string-read-fail", false));
                continue;
            }

            // Sig-pass: non-empty, length ≥ 3, matches [a-z0-9_]+
            bool passesSignature = !string.IsNullOrEmpty(id) && id.Length >= 3
                && Regex.IsMatch(id, "^[a-z0-9_]+$");

            string? failReason = passesSignature
                ? null
                : (string.IsNullOrEmpty(id) ? "empty-id" : "id-sig-fail");

            result.Add(new ProbeSample<nint>(
                $"0x{off:X}", $"0x{target:X}", ptr, failReason, passesSignature));
        }

        return result.ToArray();
    }
}