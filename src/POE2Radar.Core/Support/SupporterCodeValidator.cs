using System.Security.Cryptography;
using System.Text;

namespace POE2Radar.Core.Support;

/// <summary>
/// Support — v0.27 (LO ask): validates a user-entered supporter code against a shipped SHA-256 hash
/// list. Codes are distributed by LO to Ko-fi supporters (email / Discord DM). Nothing in the tool's
/// FUNCTIONAL surface is gated on this — the validator only unlocks COSMETIC perks (dashboard
/// palettes + optional overlay badge). Any determined user can crack a SHA-256 with a wordlist; the
/// gate exists as a lightweight social honor system, not a security boundary.
///
/// To add a new code: compute its SHA-256 (lowercase hex, no dashes) and append it to <see cref="Hashes"/>
/// below. Old codes stay valid — never remove entries.
/// </summary>
public static class SupporterCodeValidator
{
    // SHA-256 hex digests of shipped codes. Each digest = SHA256(UTF-8 encoding of the raw code text
    // with no whitespace, lowercase, hex-encoded, no dashes). Compute a new one with e.g.:
    //   echo -n "POE2GPS-COFFEE-2026" | openssl dgst -sha256 -hex
    // …then append below. The RAW code stays private (only shared via Ko-fi DM); this list ships in
    // every release.
    //
    // Seed codes (LO's initial batch, 2026-07-10):
    //   POE2GPS-COFFEE-2026 → 4c8c6bc5c96a5e4a3ed4c00a4f2f4b1a6a1a6b6d2a1e5a5b4a5b7e5f5b7a3e5b7
    //     (that hash is a PLACEHOLDER — LO must regenerate for the actual code before shipping)
    private static readonly HashSet<string> Hashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Placeholder — real hashes go here. Ship-time invariant is that at least one valid code
        // exists so the feature is discoverable via the Ko-fi email flow.
        "9d3b8e57e9b3c9d8e4f5c1a2b0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9",
    };

    /// <summary>Returns true when the trimmed code matches any shipped hash. Empty / null → false.
    /// Trims whitespace and folds to invariant lowercase before hashing so users pasting from an
    /// email don't get burned by trailing newlines or Ko-fi-side casing tweaks.</summary>
    public static bool IsSupporter(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = code.Trim().ToLowerInvariant();
        var digest = Sha256Hex(normalized);
        return Hashes.Contains(digest);
    }

    /// <summary>Public helper so LO can regenerate a hash from a raw code without going to the shell.
    /// Same normalization the validator uses at check time.</summary>
    public static string ComputeHash(string rawCode)
        => Sha256Hex((rawCode ?? "").Trim().ToLowerInvariant());

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
