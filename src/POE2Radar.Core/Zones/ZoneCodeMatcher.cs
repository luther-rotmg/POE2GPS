using System.Text.RegularExpressions;

namespace POE2Radar.Core.Zones;

/// <summary>
/// Wildcard-glob matcher for POE2 zone codes. Supports <c>*</c> matching 0-or-more characters.
/// Case-sensitive (zone codes are exact game-memory strings; case sensitivity prevents accidental
/// matches on shape drift).
/// </summary>
/// <remarks>
/// Consumed by the zone-filter preset system (A2 renderer filter, A3 dashboard preview),
/// and the auto-swap-on-zone-change feature (B3, B4 dashboard editor validation).
/// Callers that need repeated matching against the same pattern should pre-compile the
/// <see cref="Regex"/> themselves; this method is a one-shot convenience wrapper.
/// </remarks>
public static class ZoneCodeMatcher
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="zoneCode"/> matches <paramref name="pattern"/>
    /// under the supported glob syntax.
    /// </summary>
    /// <param name="pattern">
    /// A glob pattern where <c>*</c> matches 0-or-more of any character.
    /// No other metacharacters are recognised; non-<c>*</c> characters match literally.
    /// <c>null</c> never matches.
    /// </param>
    /// <param name="zoneCode">
    /// The zone code string to test. <c>null</c> never matches.
    /// Empty string <c>""</c> matches only an empty pattern (or a bare <c>*</c>).
    /// </param>
    /// <returns><c>true</c> on match, <c>false</c> otherwise (never throws).</returns>
    public static bool Match(string pattern, string zoneCode)
    {
        if (pattern is null || zoneCode is null) return false;
        var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(zoneCode, rx);
    }
}
