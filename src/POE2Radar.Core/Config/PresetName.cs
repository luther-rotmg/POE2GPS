using System.Text;
using System.Text.RegularExpressions;

namespace POE2Radar.Core.Config;

/// <summary>Sanitizes a user-supplied preset name into a safe filename stem — no path traversal, no
/// separators, bounded length. Pure + unit-tested.</summary>
public static class PresetName
{
    public const string Fallback = "preset";
    public const int MaxLength = 48;

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Fallback;
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or ' ')
                sb.Append(c);        // everything else (./\\:*?… and any path separator) is dropped
        }
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (s.Length > MaxLength) s = s[..MaxLength].Trim();
        return s.Length == 0 ? Fallback : s;
    }
}
