using System.Collections.Generic;

namespace POE2Radar.Core.RadarFilters;

/// <summary>
/// A single radar filter preset: a match pattern (glob) with whitelist and
/// blacklist entries (entity metadata path patterns).
/// </summary>
public sealed record RadarFilterPreset(
    string Match,
    IReadOnlyList<string> Whitelist,
    IReadOnlyList<string> Blacklist);

/// <summary>
/// The on-disk radar filters file: schema version plus a list of presets.
/// </summary>
public sealed record RadarFilterFile(
    int SchemaVersion,
    IReadOnlyList<RadarFilterPreset> Presets);