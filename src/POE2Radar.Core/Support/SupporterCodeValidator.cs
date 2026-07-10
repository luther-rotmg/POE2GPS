using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace POE2Radar.Core.Support;

/// <summary>
/// Support — v0.27 (LO ask): validates a user-entered supporter code against a shipped SHA-256 hash
/// list. Codes are distributed by LO to Ko-fi supporters (email / Discord DM). Nothing in the tool's
/// FUNCTIONAL surface is gated on this — the validator only unlocks COSMETIC perks (dashboard
/// palettes + optional overlay badge). Any determined user can crack a SHA-256 with a wordlist; the
/// gate exists as a lightweight social honor system, not a security boundary.
///
/// Support automation — v0.27.1 (LO ask): the hash list migrated OUT of C# and INTO an embedded
/// <c>supporter_hashes.json</c> resource so LO edits ONE JSON file to add a code (no C# recompile,
/// no `openssl` shell round-trip). Add new codes via the dashboard's Support → Maintainer helper card
/// (Settings tab, <c>?admin=1</c> URL param): paste raw code, get the SHA-256, click Copy, drop into
/// <c>supporter_hashes.json</c>.
/// </summary>
public static class SupporterCodeValidator
{
    private static readonly Lazy<HashSet<string>> _hashes =
        new(LoadHashes, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Number of shipped hashes — useful for maintainer-helper "known code count" display.</summary>
    public static int HashCount => _hashes.Value.Count;

    /// <summary>Returns true when the trimmed code matches any shipped hash. Empty / null → false.
    /// Trims whitespace and folds to invariant lowercase before hashing so users pasting from an
    /// email don't get burned by trailing newlines or Ko-fi-side casing tweaks.</summary>
    public static bool IsSupporter(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = code.Trim().ToLowerInvariant();
        var digest = Sha256Hex(normalized);
        return _hashes.Value.Contains(digest);
    }

    /// <summary>Load the shipped SHA-256 digests from the embedded supporter_hashes.json resource.
    /// Missing / malformed → empty set (fail closed; no code validates). Tolerant of the maintainer
    /// note key so an editor comment can't break parsing.</summary>
    private static HashSet<string> LoadHashes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("supporter_hashes"));
            if (name == null) return set;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return set;
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("hashes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return set;
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: 64 } hex)
                    set.Add(hex.ToLowerInvariant());
        }
        catch { /* fail closed — an unparseable resource means no code validates. */ }
        return set;
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
