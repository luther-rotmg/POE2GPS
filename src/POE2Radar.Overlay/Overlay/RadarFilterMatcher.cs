using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using POE2Radar.Core.RadarFilters;
using POE2Radar.Core.Zones;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Compiles a <see cref="RadarFilterFile"/> into a set of <see cref="Regex"/> patterns
/// for hot-path blacklist (and later, whitelist) matching on the overlay render thread.
/// </summary>
public static class RadarFilterMatcher
{
    /// <summary>
    /// Compile the blacklist patterns from the first matching preset in <paramref name="file"/>.
    /// Returns an empty array if no preset matches <paramref name="zoneCode"/> or if the
    /// matching preset has an empty blacklist.
    /// </summary>
    /// <remarks>
    /// Each glob pattern is converted to a regex via the same transformation used by
    /// <see cref="ZoneCodeMatcher"/>: <c>Regex.Escape</c> then <c>\*</c> → <c>.*</c>,
    /// anchored with <c>^</c> and <c>$</c>. Patterns are compiled with
    /// <c>RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant</c>.
    /// </remarks>
    public static Regex[] CompileBlacklist(RadarFilterFile file, string zoneCode)
    {
        if (file == null || string.IsNullOrEmpty(zoneCode))
            return Array.Empty<Regex>();

        RadarFilterPreset? matchingPreset = null;
        foreach (var preset in file.Presets)
        {
            if (ZoneCodeMatcher.Match(preset.Match, zoneCode))
            {
                matchingPreset = preset;
                break;
            }
        }

        if (matchingPreset == null || matchingPreset.Blacklist == null || matchingPreset.Blacklist.Count == 0)
            return Array.Empty<Regex>();

        var regexes = new Regex[matchingPreset.Blacklist.Count];
        for (int i = 0; i < matchingPreset.Blacklist.Count; i++)
        {
            var pattern = matchingPreset.Blacklist[i];
            // Same glob→regex conversion as ZoneCodeMatcher: escape all, then unescape * → .*
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            regexes[i] = new Regex(rx, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return regexes;
    }
}